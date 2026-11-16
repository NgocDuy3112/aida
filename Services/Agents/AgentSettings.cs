using System.Text.Json;
using aida.Data;

namespace aida.Services.Agents;

internal static class AgentSettings
{
    internal static CrewState Load(AppUser? user, IConfiguration config)
    {
        if (user is null)
            return CrewState.FromConfig(config);

        var json = user.AgentSettingsJson;
        if (string.IsNullOrWhiteSpace(json))
            return CrewState.FromConfig(config);

        try
        {
            var loaded = JsonSerializer.Deserialize<CrewState>(json);
            if (loaded is not null)
                return loaded;
        }
        catch (JsonException)
        {
        }

        return CrewState.FromConfig(config);
    }

    internal static string Serialize(CrewState state)
        => JsonSerializer.Serialize(state);
}
