using System.Text.Json;
using Microsoft.Extensions.AI;

namespace aida.Services.Agents;

internal static class AskUserTool
{
    internal const string Name = "ask_user";

    internal const string Description =
        "Hỏi người dùng một câu hỏi làm rõ khi yêu cầu mơ hồ hoặc thiếu thông tin. Tool trả về câu trả lời " +
        "của người dùng ngay trong lượt này, nên chỉ gọi khi thật sự cần.";

    internal const string Instructions =
        "- Khi thiếu thông tin mà không thể suy ra hoặc tìm trong tài liệu, hãy gọi tool ask_user với một câu hỏi " +
        "ngắn bằng tiếng Việt, kèm 1-4 lựa chọn ngắn khi câu trả lời chỉ có vài khả năng. Tool trả về câu trả lời " +
        "của người dùng ngay trong lượt này: chờ kết quả rồi viết tiếp câu trả lời. Có thể hỏi nhiều câu liên tiếp " +
        "nếu cần, nhưng mỗi lần một câu và không hỏi lại điều đã có trong ngữ cảnh. Không viết câu hỏi vào phần " +
        "trả lời. Nếu có thể trả lời trực tiếp thì không dùng tool này. Nếu kết quả bắt đầu bằng " +
        "\"user sent a new message instead:\" thì người dùng đã gửi tin nhắn mới thay vì trả lời: làm theo nội dung " +
        "tin nhắn đó, đừng coi là người dùng từ chối.";

    internal const string MissingQuestionResult = "Error: ask_user needs a 'question'.";

    internal static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "Câu hỏi ngắn, cụ thể, bằng tiếng Việt." },
            "options": {
              "type": "array",
              "items": { "type": "string" },
              "maxItems": 4,
              "description": "1-4 lựa chọn ngắn, loại trừ lẫn nhau, bằng tiếng Việt. Bỏ trống nếu câu trả lời là văn bản tự do."
            },
            "allow_free_text": {
              "type": "boolean",
              "description": "Cho phép người dùng gõ câu trả lời tự do ngoài các lựa chọn. Mặc định true."
            }
          },
          "required": ["question"]
        }
        """).RootElement.Clone();

    /// <summary>Builds the real ask_user tool: the call blocks on <paramref name="broker"/> until answered.</summary>
    /// <param name="onResolved">
    /// Awaited with the resolved exchange before the tool result reaches the model, so the conversation can
    /// keep the question and its answer.
    /// </param>
    internal static AIFunction Create(AskUserBroker broker, Func<AskUserExchange, Task>? onResolved = null) =>
        new AskUserFunction(broker, onResolved);
}

internal sealed class AskUserFunction(AskUserBroker broker, Func<AskUserExchange, Task>? onResolved = null) : AIFunction
{
    public override string Name => AskUserTool.Name;

    public override string Description => AskUserTool.Description;

    public override JsonElement JsonSchema => AskUserTool.Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var question = AskUserArguments.Question(arguments)?.Trim();
        if (string.IsNullOrWhiteSpace(question))
            return AskUserTool.MissingQuestionResult;

        var options = AskUserArguments.Options(arguments)
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Take(4)
            .ToList();

        return await broker.AskAsync(
            question,
            options,
            AskUserArguments.AllowFreeText(arguments),
            cancellationToken,
            onResolved).ConfigureAwait(false);
    }
}
