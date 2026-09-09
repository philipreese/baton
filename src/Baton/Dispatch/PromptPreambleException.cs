namespace Baton.Dispatch;

/// <summary>
/// #1373: a <see cref="CoreDispatchTarget"/> whose <c>PromptText</c> is set but is not also one of its
/// <c>Args</c> — see <see cref="CoreDispatchTarget.WithPromptPreamble"/> and
/// <see cref="CoreDispatchTarget.WithReplacedPrompt"/> for the invariant and why a break in it is
/// refused rather than silently dropped.
/// </summary>
public sealed class PromptPreambleException : BatonFlowException
{
    public PromptPreambleException(string message)
        : base(message)
    {
        TryInvocation = "this is an adapter defect, not an operator one — report it with the room id; "
            + "no continuation brief can be delivered until the adapter passes its prompt as both an "
            + "argument and PromptText.";
    }
}
