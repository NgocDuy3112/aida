namespace aida.Services.Agents;

/// <summary>A question the agent is waiting on, as rendered by the chat panel.</summary>
internal sealed record AskUserQuestion(long Id, string Question, IReadOnlyList<string> Options, bool AllowFreeText);

/// <summary>How the waiting question stopped waiting for a real answer.</summary>
internal enum AskUserOutcome
{
    /// <summary>The user typed or picked an answer.</summary>
    Answered,

    /// <summary>The user skipped the question; the tool returned <see cref="AskUserBroker.DeclinedAnswer"/>.</summary>
    Declined,

    /// <summary>The user sent a new message instead; the tool returned <see cref="AskUserBroker.SteeredAnswerPrefix"/>.</summary>
    Steered,
}

/// <summary>A question the user resolved, i.e. one that is worth keeping in the conversation.</summary>
internal sealed record AskUserExchange(AskUserQuestion Question, AskUserOutcome Outcome, string Answer);

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

        /// <summary>Set by whoever answered; <c>null</c> while the question still waits.</summary>
        public AskUserOutcome? Outcome { get; set; }

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
    /// <param name="onResolved">
    /// Awaited with the exchange before the answer is returned to the caller, so what the model reads and
    /// what the conversation keeps cannot drift apart. Not called when the question is cancelled.
    /// </param>
    public async Task<string> AskAsync(
        string question, IReadOnlyList<string> options, bool allowFreeText, CancellationToken ct,
        Func<AskUserExchange, Task>? onResolved = null)
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
            var answer = await pending.Answer.Task.ConfigureAwait(false);
            if (onResolved is not null && pending.Outcome is { } outcome)
                await onResolved(new AskUserExchange(pending.Question, outcome, answer)).ConfigureAwait(false);
            return answer;
        }
        finally
        {
            ClearIfCurrent(pending);
        }
    }

    /// <summary>Completes the waiting question with <paramref name="answer"/>; false when nothing waits.</summary>
    public bool TryAnswer(string answer) => Resolve(answer, AskUserOutcome.Answered);

    /// <summary>Answers the waiting question with <see cref="DeclinedAnswer"/>.</summary>
    public bool TryDecline() => Resolve(DeclinedAnswer, AskUserOutcome.Declined);

    /// <summary>
    /// Answers the waiting question with the message the user sent instead of an answer, so the model
    /// reads a new instruction rather than a refusal.
    /// </summary>
    public bool TrySteer(string message) => Resolve(SteeredAnswer(message), AskUserOutcome.Steered);

    private bool Resolve(string answer, AskUserOutcome outcome)
    {
        Waiting? pending;
        lock (gate)
        {
            pending = current;
            current = null;
        }

        if (pending is null) return false;

        pending.Outcome = outcome;
        var accepted = pending.Answer.TrySetResult(answer.Trim());
        RaiseChanged(null);
        return accepted;
    }

    internal static string SteeredAnswer(string message)
    {
        var text = message.Trim();
        if (text.Length > SteeredTextLimit)
        {
            // Cutting inside a surrogate pair would leave the model a lone surrogate; drop one more unit.
            var end = char.IsHighSurrogate(text[SteeredTextLimit - 1]) ? SteeredTextLimit - 1 : SteeredTextLimit;
            text = text[..end] + "…";
        }
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
