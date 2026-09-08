using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised by Claude's model grammar check, called from <see cref="IWorkerAdapter.Resolve"/> and
/// <see cref="IWorkerAdapter.ValidateRequestedModel"/>, when a <c>--model</c> string is malformed for the
/// vendor's own id grammar in a way the adapter can name up-front — today, a <c>claude-*</c> id whose
/// version is dot-delimited where claude's are dash-delimited. During binding resolution it surfaces
/// at Baton.Cli's top-level <c>catch (BatonFlowException)</c>; queue add catches it as
/// <c>BatonFlowException</c> and wraps it in <c>CliArgumentException</c>. Both paths precede the
/// dispatch pump and never enter <c>RetryPolicy</c>. This is NOT model-list validation — claude ships none
/// (<c>ClaudeWorkerAdapter.ModelAliases</c>'s doc records why), so only the one distinguishable typo is
/// caught. Why the typo is worth refusing up-front, and the measured claude behaviour behind it, live
/// in <c>docs/vendor-doc-audit.md</c> §5.
/// </summary>
public sealed class MalformedVendorModelException : BatonFlowException
{
    public string AdapterName { get; }

    public MalformedVendorModelException(string adapterName, string reason)
        : base($"The '{adapterName}' adapter cannot use the requested --model: {reason}")
    {
        AdapterName = adapterName;
    }
}
