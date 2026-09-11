from pathlib import Path
import ast
import os
import re
import shutil
import subprocess
import sys
import time
import tomllib

ROOT = Path(__file__).resolve().parents[2]
BASH_SPAWNS = 0


def refused(action):
    try:
        action()
    except AssertionError:
        return
    raise AssertionError('Mutated workflow was accepted')


def field(block, name):
    values = re.findall(r'^    '+name+r': (.+)$', block, re.M)
    assert len(values) == 1, (name, values)
    return values[0]


def condition(expression, event, results=None, dotnet='true', cancelled=False):
    values = {'github.event_name': event, 'needs.changes.outputs.dotnet': dotnet}
    values.update({'needs.'+key+'.result': value for key,value in (results or {}).items()})
    expression=expression.removeprefix('${{').removesuffix('}}').strip()
    for key,value in values.items():
        expression=expression.replace(key,repr(value))
    expression=expression.replace('always()','True').replace('cancelled()',str(cancelled))
    expression=expression.replace('&&',' and ').replace('||',' or ')
    expression=re.sub(r'!(?!=)',' not ',expression).strip()
    parsed=ast.parse(expression,mode='eval')
    allowed=(ast.Expression,ast.BoolOp,ast.And,ast.Or,ast.UnaryOp,ast.Not,ast.Compare,ast.Eq,ast.NotEq,ast.Constant,ast.Load)
    assert all(isinstance(node,allowed) for node in ast.walk(parsed)),expression
    return eval(compile(parsed,'workflow condition','eval'),{'__builtins__':{}})


def jobs_from(text):
    assert not re.search(r'^\s+[^#\n]*[&*][A-Za-z_]',text,re.M)
    pieces=re.split(r'^  ([a-z_-]+):\n',text.split('jobs:\n',1)[1],flags=re.M)
    jobs=dict(zip(pieces[1::2],pieces[2::2]))
    for block in jobs.values():
        for step in re.split(r'^      - ',block,flags=re.M)[1:]:
            keys=re.findall(r'^(?:        )?([a-z][a-z-]*):',step,re.M)
            assert len(keys)==len(set(keys)),keys
            assert ('uses' in keys) != ('run' in keys),keys
            assert not ('run' in keys and 'with' in keys),keys
    return jobs


def scripts_from(block):
    matches=re.findall(r'^        run: \|\n((?:          [^\n]*\n)+)',block,re.M)
    return ['\n'.join(line[10:] for line in match.splitlines()) for match in matches]


def script_from(block):
    matches=scripts_from(block)
    assert len(matches)==1
    return matches[0]


def bash():
    if os.name=='nt':
        git=shutil.which('git')
        assert git,'Git is required by the existing toolchain'
        candidate=Path(git).resolve().parent.parent/'bin/bash.exe'
        if candidate.exists():
            return str(candidate)
    executable=shutil.which('bash')
    assert executable,'Bash is required to exercise the workflow scripts'
    return executable


def run_script(script, values):
    global BASH_SPAWNS
    BASH_SPAWNS += 1
    env={key:value for key,value in os.environ.items() if not key.startswith(('GH_','GITHUB_'))}
    env.update(values)
    # A shell function replaces the sole read-only network command; all admission and
    # aggregate decisions below are the actual workflow script, not Python replicas.
    prefix='gh() { if [ "${API_FAIL:-0}" = 1 ]; then return 7; fi; printf "%s" "$LIVE_SHA"; };\n'
    return subprocess.run([bash(),'-c',prefix+script],env=env,capture_output=True,text=True,timeout=30).returncode


def check_workflow(text):
    jobs=jobs_from(text)
    assert set(jobs)=={'recovery_target','changes','test','pack','gates','ci','recovery_ci'}
    assert field(jobs['ci'],'needs')=='[changes, test, gates]'
    assert field(jobs['test'],'needs')=='[changes, recovery_target]'
    assert field(jobs['pack'],'needs')=='[test, recovery_target]'
    assert field(jobs['gates'],'needs')=='[changes, recovery_target]'
    assert field(jobs['recovery_ci'],'needs')=='[recovery_target, test, gates, pack]'
    for event in ('push','pull_request','workflow_dispatch'):
        assert condition(field(jobs['recovery_target'],'if'),event)==(event=='workflow_dispatch')
        assert condition(field(jobs['ci'],'if'),event)==(event!='workflow_dispatch')
        assert condition(field(jobs['recovery_ci'],'if'),event)==(event=='workflow_dispatch')
        for target in ('success','skipped','failure','cancelled',''):
            for dotnet in ('true','false'):
                eligible=event!='workflow_dispatch' or target=='success'
                assert condition(field(jobs['test'],'if'),event,{'recovery_target':target},dotnet)==(eligible and (event!='pull_request' or dotnet=='true'))
            assert condition(field(jobs['gates'],'if'),event,{'recovery_target':target})==eligible
        for test in ('success','failure','skipped','cancelled',''):
            assert condition(field(jobs['pack'],'if'),event,{'test':test})==(event!='pull_request' and test=='success')
    for name in ('test','gates','pack'):
        assert '          ref: ${{ github.sha }}\n' in jobs[name]
    preflight=jobs['recovery_target']
    assert 'uses:' not in preflight and 'contents: read' in preflight
    assert 'EXPECTED_SHA: ${{ inputs.expected_sha }}' in preflight
    assert 'GH_TOKEN: ${{ github.token }}' in preflight
    assert 'continue-on-error' not in preflight
    target_script=script_from(preflight)
    sha='a'*40
    env=dict(GITHUB_REF='refs/heads/main',GITHUB_SHA=sha,EXPECTED_SHA=sha,
             GITHUB_REPOSITORY='owner/repo',LIVE_SHA=sha)
    assert run_script(target_script,env)==0
    for changes in ({'GITHUB_REF':'refs/heads/topic'},{'EXPECTED_SHA':''},
                    {'EXPECTED_SHA':'b'*40},{'LIVE_SHA':'b'*40},{'API_FAIL':'1'}):
        assert run_script(target_script,env|changes)!=0,changes
    aggregate=jobs['recovery_ci']
    assert 'uses:' not in aggregate and 'continue-on-error' not in aggregate
    for key,job in [('TARGET','recovery_target'),('TEST','test'),('GATES','gates'),('PACK','pack')]:
        assert key+'_RESULT: ${{ needs.'+job+'.result }}' in aggregate
    assert 'GATES_MODE: ${{ needs.gates.outputs.coverage-mode }}' in aggregate
    results={key+'_RESULT':'success' for key in ('TARGET','TEST','GATES','PACK')} | {'GATES_MODE':'test-shard-complement'}
    aggregate_script=script_from(aggregate)
    assert run_script(aggregate_script,results)==0
    for key in results:
        for status in ('failure','cancelled','skipped','') if key != 'GATES_MODE' else ('full',''):
            assert run_script(aggregate_script,results|{key:status})!=0


def check_release_workflow(text):
    jobs=jobs_from(text)
    assert set(jobs)=={'recovery_target','release-please','release_recovery'}
    target=jobs['recovery_target']
    release=jobs['release-please']
    aggregate=jobs['release_recovery']
    assert field(release,'needs')=='[recovery_target]'
    assert field(aggregate,'needs')=='[recovery_target, release-please]'
    assert 'group: release-please-${{ github.ref }}' in text
    assert 'cancel-in-progress: true' in text
    assert text.count('uses: googleapis/release-please-action@v4')==1
    assert 'token: ${{ secrets.BATON_RELEASE_PLEASE }}' in release
    assert 'continue-on-error' not in text
    assert release.count("if: github.event_name == 'workflow_dispatch'")==1
    assert release.count("if: ${{ always() && github.event_name == 'workflow_dispatch' }}")==1
    assert (release.index('name: Revalidate selected main revision') <
            release.index('uses: googleapis/release-please-action@v4') <
            release.index('name: Detect concurrent main advance'))
    for event in ('push','workflow_dispatch'):
        assert condition(field(target,'if'),event)==(event=='workflow_dispatch')
        assert condition(field(aggregate,'if'),event)==(event=='workflow_dispatch')
        for target_result in ('success','failure','cancelled','skipped',''):
            expected=event=='push' or target_result=='success'
            assert condition(field(release,'if'),event,{'recovery_target':target_result})==expected

    assert 'uses:' not in target and 'contents: read' in target
    assert 'EXPECTED_SHA: ${{ inputs.expected_sha }}' in target
    assert 'GH_TOKEN: ${{ github.token }}' in target
    target_script=script_from(target)
    release_scripts=scripts_from(release)
    assert len(release_scripts)==2
    revalidate_script,advance_script=release_scripts
    sha='a'*40
    env=dict(GITHUB_REF='refs/heads/main',GITHUB_SHA=sha,EXPECTED_SHA=sha,
             GITHUB_REPOSITORY='owner/repo',LIVE_SHA=sha)
    for script in (target_script,revalidate_script):
        assert run_script(script,env)==0
        for changes in ({'GITHUB_REF':'refs/heads/topic'},{'EXPECTED_SHA':''},
                        {'EXPECTED_SHA':'bad','GITHUB_SHA':'bad','LIVE_SHA':'bad'},
                        {'EXPECTED_SHA':'b'*40},{'GITHUB_SHA':'b'*40},
                        {'LIVE_SHA':'b'*40},{'API_FAIL':'1'}):
            assert run_script(script,env|changes)!=0,changes
    assert run_script(advance_script,env)==0
    assert run_script(advance_script,env|{'LIVE_SHA':'b'*40})!=0
    assert run_script(advance_script,env|{'API_FAIL':'1'})!=0

    # A repeated admission is state-free and reaches the same sole release owner. The
    # action then applies its existing manifest/tag state, so update and no-op semantics
    # stay with release-please rather than a second release implementation here.
    for _ in range(2):
        assert run_script(target_script,env)==0
        assert run_script(revalidate_script,env)==0
        assert run_script(advance_script,env)==0

    assert 'uses:' not in aggregate and 'continue-on-error' not in aggregate
    for key,job in [('TARGET','recovery_target'),('RELEASE','release-please')]:
        assert key+'_RESULT: ${{ needs.'+job+'.result }}' in aggregate
    results={'TARGET_RESULT':'success','RELEASE_RESULT':'success'}
    aggregate_script=script_from(aggregate)
    assert run_script(aggregate_script,results)==0
    for key in results:
        for status in ('failure','cancelled','skipped',''):
            assert run_script(aggregate_script,results|{key:status})!=0


def _sha_environment():
    sha='a'*40
    return dict(GITHUB_REF='refs/heads/main',GITHUB_SHA=sha,EXPECTED_SHA=sha,
                GITHUB_REPOSITORY='owner/repo',LIVE_SHA=sha)


def check_ci_pack_success(text):
    expression=field(jobs_from(text)['pack'],'if')
    assert condition(expression,'workflow_dispatch',{'test':'success'})
    assert not condition(expression,'workflow_dispatch',{'test':'failure'})


def check_ci_live_sha(text):
    script=script_from(jobs_from(text)['recovery_target'])
    env=_sha_environment()
    assert run_script(script,env)==0
    assert run_script(script,env|{'LIVE_SHA':'b'*40})!=0


def check_ci_aggregate_needs(text):
    assert field(jobs_from(text)['recovery_ci'],'needs')=='[recovery_target, test, gates, pack]'


def check_ci_pack_input(text):
    assert 'PACK_RESULT: ${{ needs.pack.result }}' in jobs_from(text)['recovery_ci']


def check_ci_gates_mode_input(text):
    assert 'GATES_MODE: ${{ needs.gates.outputs.coverage-mode }}' in jobs_from(text)['recovery_ci']


def check_ci_step_shape(text):
    jobs_from(text)


def _release_scripts(text):
    jobs=jobs_from(text)
    target_script=script_from(jobs['recovery_target'])
    release_scripts=scripts_from(jobs['release-please'])
    assert len(release_scripts)==2
    return jobs, target_script, release_scripts


def check_release_sha_format(text):
    _,target_script,_=_release_scripts(text)
    env=_sha_environment()
    assert run_script(target_script,env)==0
    assert run_script(target_script,env|{'EXPECTED_SHA':'bad','GITHUB_SHA':'bad','LIVE_SHA':'bad'})!=0


def check_release_live_sha(text):
    _,target_script,_=_release_scripts(text)
    env=_sha_environment()
    assert run_script(target_script,env)==0
    assert run_script(target_script,env|{'LIVE_SHA':'b'*40})!=0


def check_release_target_condition(text):
    expression=field(jobs_from(text)['release-please'],'if')
    assert condition(expression,'workflow_dispatch',{'recovery_target':'success'})
    assert not condition(expression,'workflow_dispatch',{'recovery_target':'failure'})


def check_release_aggregate_needs(text):
    assert field(jobs_from(text)['release_recovery'],'needs')=='[recovery_target, release-please]'


def check_release_aggregate_result(text, key):
    aggregate=jobs_from(text)['release_recovery']
    results={'TARGET_RESULT':'success','RELEASE_RESULT':'success'}
    script=script_from(aggregate)
    assert run_script(script,results)==0
    assert run_script(script,results|{key:''})!=0


def check_release_result_input(text):
    assert 'RELEASE_RESULT: ${{ needs.release-please.result }}' in jobs_from(text)['release_recovery']


def _control(name, ci_text, release_text):
    controls={
        'ci-pack-success': lambda: check_ci_pack_success(ci_text),
        'ci-live-sha': lambda: check_ci_live_sha(ci_text),
        'ci-aggregate-needs': lambda: check_ci_aggregate_needs(ci_text),
        'ci-pack-input': lambda: check_ci_pack_input(ci_text),
        'ci-gates-mode-input': lambda: check_ci_gates_mode_input(ci_text),
        'ci-step-shape': lambda: check_ci_step_shape(ci_text),
        'release-sha-format': lambda: check_release_sha_format(release_text),
        'release-live-sha': lambda: check_release_live_sha(release_text),
        'release-target-condition': lambda: check_release_target_condition(release_text),
        'release-aggregate-needs': lambda: check_release_aggregate_needs(release_text),
        'release-target-result': lambda: check_release_aggregate_result(release_text,'TARGET_RESULT'),
        'release-release-result': lambda: check_release_aggregate_result(release_text,'RELEASE_RESULT'),
        'release-result-input': lambda: check_release_result_input(release_text),
    }
    assert name in controls, f'unknown workflow recovery control: {name}'
    controls[name]()


def _check_mutations(text, mutations, ci_text, release_text):
    for before,after,control in mutations:
        assert before in text
        refused(lambda before=before,after=after,control=control:
                _control(control,ci_text.replace(before,after),release_text.replace(before,after)))


def main(argv=None):
    global BASH_SPAWNS
    BASH_SPAWNS=0
    argv=sys.argv[1:] if argv is None else argv
    ci_text=(ROOT/'.github/workflows/ci.yml').read_text(encoding='utf-8')
    release_text=(ROOT/'.github/workflows/release-please.yml').read_text(encoding='utf-8')
    if argv:
        assert len(argv)==1
        _control(argv[0],ci_text,release_text)
        print(f'workflow recovery control {argv[0]} passed; Git Bash spawns={BASH_SPAWNS}')
        return

    started=time.perf_counter()
    phase=time.perf_counter()
    check_workflow(ci_text)
    ci_baseline_seconds=time.perf_counter()-phase
    phase=time.perf_counter()
    _check_mutations(ci_text,(
        ("needs.test.result == 'success'","needs.test.result != 'success'",'ci-pack-success'),
        ('[ "$live_sha" != "$EXPECTED_SHA" ]','[ "$live_sha" = "$EXPECTED_SHA" ]','ci-live-sha'),
        ('needs: [recovery_target, test, gates, pack]','needs: [test, gates]','ci-aggregate-needs'),
        ('PACK_RESULT: ${{ needs.pack.result }}','PACK_RESULT: success','ci-pack-input'),
        ('GATES_MODE: ${{ needs.gates.outputs.coverage-mode }}','GATES_MODE: full','ci-gates-mode-input'),
        ('        shell: bash\n','        with:\n          fetch-depth: 0\n        shell: bash\n','ci-step-shape')),
        ci_text,release_text)
    ci_controls_seconds=time.perf_counter()-phase
    phase=time.perf_counter()
    check_release_workflow(release_text)
    release_baseline_seconds=time.perf_counter()-phase
    phase=time.perf_counter()
    _check_mutations(release_text,(
        (' || [[ ! "$EXPECTED_SHA" =~ ^[0-9a-f]{40}$ ]]','','release-sha-format'),
        ('[ "$live_sha" != "$EXPECTED_SHA" ]','[ "$live_sha" = "$EXPECTED_SHA" ]','release-live-sha'),
        ("needs.recovery_target.result == 'success'","needs.recovery_target.result != 'success'",'release-target-condition'),
        ('needs: [recovery_target, release-please]','needs: [release-please]','release-aggregate-needs'),
        ('[ "$TARGET_RESULT" != "success" ]','[ -n "$TARGET_RESULT" ] && [ "$TARGET_RESULT" != "success" ]','release-target-result'),
        ('[ "$RELEASE_RESULT" != "success" ]','[ -n "$RELEASE_RESULT" ] && [ "$RELEASE_RESULT" != "success" ]','release-release-result'),
        ('RELEASE_RESULT: ${{ needs.release-please.result }}','RELEASE_RESULT: success','release-result-input')),
        ci_text,release_text)
    release_controls_seconds=time.perf_counter()-phase
    pixi=tomllib.loads((ROOT/'pixi.toml').read_text(encoding='utf-8'))
    assert pixi['tasks']['workflow-recovery-selftest']['cmd']=='python tools/workflow-recovery/selftest.py'
    gates=ast.parse((ROOT/'tools/gates/gates.py').read_text(encoding='utf-8'))
    overlap=next(node.value for node in gates.body if isinstance(node,ast.Assign)
                 and any(isinstance(target,ast.Name) and target.id=='OVERLAP' for target in node.targets))
    assert 'workflow-recovery-selftest' in ast.literal_eval(overlap)
    print('Actual CI/release preflight and aggregate scripts, event conditions, races, repeats, step shape and mutations passed; '
          f'Git Bash spawns={BASH_SPAWNS}; elapsed={time.perf_counter()-started:.3f}s '
          f'(CI baseline={ci_baseline_seconds:.3f}s, CI controls={ci_controls_seconds:.3f}s, '
          f'release baseline={release_baseline_seconds:.3f}s, release controls={release_controls_seconds:.3f}s)')


if __name__=='__main__':
    main()
