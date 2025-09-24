using System.Text.Json;
using System.Text.Json.Serialization;

namespace SS.Matchmaking.League
{
    /// <summary>
    /// Information about a league game.
    /// </summary>
    public class LeagueGameInfo
    {
        /// <summary>
        /// ID of the game in the league database.
        /// </summary>
        [JsonPropertyName("season_game_id")]
        public long SeasonGameId { get; init; }

        /// <summary>
        /// The game type in the league database. This is used to determine which game mode to hand off running the game to.
        /// </summary>
        [JsonPropertyName("game_type_id")]
        public long GameTypeId { get; init; }

        /// <summary>
        /// ID of the league in the league database.
        /// </summary>
        [JsonPropertyName("league_id")]
        public required long LeagueId { get; init; }

        /// <summary>
        /// The name of the league.
        /// </summary>
        [JsonPropertyName("league_name")]
        public required string LeagueName { get; init; }

        /// <summary>
        /// ID of the season in the league database.
        /// </summary>
        [JsonPropertyName("season_id")]
        public required long SeasonId { get; init; }

        /// <summary>
        /// The name of the season within the league.
        /// </summary>
        [JsonPropertyName("season_name")]
        public required string SeasonName { get; init; }

        /// <summary>
        /// The round number the game is in. This is an optional # that can help organize (group) games within a season.
        /// </summary>
        [JsonPropertyName("round_number")]
        public int? RoundNumber { get; init; }

        /// <summary>
        /// Name of the round.
        /// </summary>
        [JsonPropertyName("round_name")]
        public string? RoundName { get; init; }

        /// <summary>
        /// When the league game is scheduled to be played.
        /// </summary>
        [JsonPropertyName("scheduled_timestamp")]
        public DateTime? ScheduledTimestamp { get; init; }

        /// <summary>
        /// The teams in the league game.
        /// </summary>
        /// <remarks>
        /// Key: freq, Value: team info
        /// </remarks>
        [JsonPropertyName("teams")]
        public required SortedDictionary<short, LeagueTeamInfo> Teams { get; init; }
    }

    /// <summary>
    /// Information about a team in a league game.
    /// </summary>
    public class LeagueTeamInfo
    {
        /// <summary>
        /// ID of the team in the league database.
        /// </summary>
        [JsonPropertyName("team_id")]
        public long TeamId { get; init; }

        /// <summary>
        /// Name of the team.
        /// </summary>
        [JsonPropertyName("team_name")]
        public required string TeamName { get; init; }

        /// <summary>
        /// The players on the team's roster that can participate.
        /// </summary>
        /// <remarks>
        /// Key: player name (case-insensitive), Value: is captain
        /// </remarks>
        [JsonPropertyName("roster")]
        [JsonConverter(typeof(CaseInsensitiveDictionaryConverter<bool>))]
        public required Dictionary<string, bool> Roster { get; set; }

        //[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        //public Dictionary<string, bool> Roster { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Unfortunately, the populate behavior currently doesn't work for types that have a parameterized constructor.
        // Perhaps Microsoft will enhance the Populate functionality in a later .NET release? Leaving this here as a reminder to check.
        // The JsonConverter solution used feels hacky. Though it works, it creates an extra case-sensitive Dictionary.
    }

    // Based on: https://stackoverflow.com/questions/67307699/deserialize-into-a-case-insensitive-dictionary-using-system-text-json
    public sealed class CaseInsensitiveDictionaryConverter<TValue> : JsonConverter<Dictionary<string, TValue>>
    {
        public override Dictionary<string, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Deserialize normally (into a case-sensitive dictionary). Then, copy it to a case insensitive dictionary.
            return JsonSerializer.Deserialize(ref reader, typeToConvert, options) is not Dictionary<string, TValue> dictionary
                ? []
                : new Dictionary<string, TValue>(dictionary, StringComparer.OrdinalIgnoreCase);
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, TValue> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, typeof(TValue), options);
        }
    }

    // For using the System.Text.Json source generator.
    [JsonSerializable(typeof(LeagueGameInfo), GenerationMode = JsonSourceGenerationMode.Metadata)]
    internal partial class SourceGenerationContext : JsonSerializerContext
    {
    }
}
