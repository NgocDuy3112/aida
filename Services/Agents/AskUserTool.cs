using System.Text.Json;
using Microsoft.Extensions.AI;

namespace aida.Services.Agents;

internal static class AskUserTool
{
    internal const string Name = "ask_user";

    internal const string Instructions =
        "- Khi thiếu thông tin mà không thể suy ra hoặc tìm trong tài liệu, hãy gọi tool ask_user " +
        "với một câu hỏi ngắn bằng tiếng Việt và 2-4 lựa chọn ngắn. Không viết câu hỏi vào phần trả lời. " +
        "Chỉ gọi tối đa một lần mỗi lượt và không gọi tool khác cùng lúc. " +
        "Nếu có thể trả lời trực tiếp thì không dùng tool này.";

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "Câu hỏi ngắn, cụ thể, bằng tiếng Việt." },
            "options": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 2,
              "maxItems": 4,
              "description": "2-4 lựa chọn ngắn, loại trừ lẫn nhau, bằng tiếng Việt."
            }
          },
          "required": ["question", "options"]
        }
        """).RootElement.Clone();

    internal static AIFunctionDeclaration Create() =>
        AIFunctionFactory.CreateDeclaration(
            Name,
            "Hỏi người dùng một câu hỏi làm rõ (kèm lựa chọn) khi yêu cầu mơ hồ hoặc thiếu thông tin.",
            Schema);
}