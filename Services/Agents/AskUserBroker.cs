namespace aida.Services.Agents;

/// <summary>A question the agent is waiting on, as rendered by the chat panel.</summary>
internal sealed record AskUserQuestion(long Id, string Question, IReadOnlyList<string> Options, bool AllowFreeText);

/// <summary>
/// Bridges the blocking ask_user tool call and the chat UI. The tool registers a question and awaits
/// it inside the running agent turn; the UI answers or declines it. Cancellation (<see cref="Cancel"/>)
/// releases a waiting tool so a stopped, steered or disposed conversation never leaves the turn hung.
/// </summary>
internal sealed class AskUserBroker
{
    internal const string DeclinedAnswer = "user declined to answer";

    internal const string SteeredAnswerPrefix = "user sent a new message instead: ";

    /// <summary>Longer steer messages are cut here so the model sees a signal, not a whole transcript.</summary>
    private const int SteeredTextLimit = 200;

    private sealed class Waiting(AskUserQuestion question)
    {
        public AskUserQuestion Question { get; } = question;

        public TaskCompletionSource<string> Answer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new();
    private Waiting? current;
    private long nextId;

    /// <summary>Raised when the waiting question changes; <c>null</c> means nothing is waiting.</summary>
    public event Action<AskUserQuestion?>? Changed;

    public AskUserQuestion? Pending
    {
        get { lock (gate) return current?.Question; }
    }

    /// <summary>
    /// Registers <paramref name="question"/> and waits for the answer. Returns the user's text, or
    /// <see cref="DeclinedAnswer"/> when the user skips. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> or <see cref="Cancel"/> releases the question.
    /// </summary>
    public async Task<string> AskAsync(
        string question, IReadOnlyList<string> options, bool allowFreeText, CancellationToken ct)
    {
        Waiting pending;
        lock (gate)
        {
            pending = new Waiting(new AskUserQuestion(++nextId, question, options, allowFreeText));
            var replaced = current;
            current = pending;
            // A second concurrent call can only mean the previous question lost its panel.
            replaced?.Answer.TrySetResult(DeclinedAnswer);
        }

        try
        {
            RaiseChanged(pending);
            using var registration = ct.Register(() => pending.Answer.TrySetCanceled(ct));
            return await pending.Answer.Task.ConfigureAwait(false);
        }
        finally
        {
            ClearIfCurrent(pending);
        }
    }

    /// <summary>Completes the waiting question with <paramref name="answer"/>; false when nothing waits.</summary>
    public bool TryAnswer(string answer)
    {
        Waiting? pending;
        lock (gate)
        {
            pending = current;
            current = null;
        }

        if (pending is null) return false;

        var accepted = pending.Answer.TrySetResult(answer.Trim());
        RaiseChanged(null);
        return accepted;
    }

    /// <summary>Answers the waiting question with <see cref="DeclinedAnswer"/>.</summary>
    public bool TryDecline() => TryAnswer(DeclinedAnswer);

    /// <summary>
    /// Answers the waiting question with the message the user sent instead of an answer, so the model
    /// reads a new instruction rather than a refusal.
    /// </summary>
    public bool TrySteer(string message) => TryAnswer(SteeredAnswer(message));

    internal static string SteeredAnswer(string message)
    {
        var text = message.Trim();
        if (text.Length > SteeredTextLimit)
            text = text[..SteeredTextLimit] + "…";
        return SteeredAnswerPrefix + text;
    }

    /// <summary>Releases a waiting question by cancelling it, so the tool call does not hang.</summary>
    public void Cancel()
    {
        Waiting? pending;
        lock (gate)
        {
            pending = current;
            current = null;
        }

        if (pending is null) return;

        pending.Answer.TrySetCanceled();
        RaiseChanged(null);
    }

    private void ClearIfCurrent(Waiting pending)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, pending)) return;
            current = null;
        }

        RaiseChanged(null);
    }

    /// <summary>
    /// Raises <see cref="Changed"/> with the state of <paramref name="waiting"/>, but only while that
    /// state is still the current one: a turn that has already been answered, declined or cancelled
    /// must not overwrite the panel of the question that replaced it.
    /// </summary>
    private void RaiseChanged(Waiting? waiting)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, waiting)) return;
        }

        Changed?.Invoke(waiting?.Question);
    }
}
