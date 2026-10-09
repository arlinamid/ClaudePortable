using System.Text.Json.Serialization;

namespace ClaudePortable.Core.Manifest;

public enum RetentionTier
{
    Daily,
    Weekly,
    Monthly,
}

public sealed record BackupManifest
{
    public const int CurrentSchemaVersion = 2;

    // The JSON source generator assigns every init-only property, using
    // null for fields missing from older manifests (no archiveTargets
    // before 0.1.13, no links before AgentPortable). Collections are
    // therefore normalised here so readers never see null.
    private readonly Dictionary<string, string> _sourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _archiveTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<string> _excludedPaths = Array.Empty<string>();
    private readonly IReadOnlyList<BackupLink> _links = Array.Empty<BackupLink>();

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    [JsonPropertyName("windowsUser")]
    public string WindowsUser { get; init; } = string.Empty;

    [JsonPropertyName("claudeDesktopVersion")]
    public string? ClaudeDesktopVersion { get; init; }

    [JsonPropertyName("claudeCodeVersion")]
    public string? ClaudeCodeVersion { get; init; }

    [JsonPropertyName("retentionTier")]
    [JsonConverter(typeof(JsonStringEnumConverter<RetentionTier>))]
    public RetentionTier RetentionTier { get; init; } = RetentionTier.Daily;

    [JsonPropertyName("sourcePaths")]
    public Dictionary<string, string> SourcePaths
    {
        get => _sourcePaths;
        init => _sourcePaths = value ?? new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps archive-prefix (as it appears inside the zip, e.g.
    /// "claude-desktop/appdata" or "cowork-projects/a7b3") to the
    /// absolute path the files came from on the backup machine.
    /// Restore reads this to know where to write each archived tree.
    /// Added in 2026-04-23 alongside Cowork-project auto-backup.
    /// </summary>
    [JsonPropertyName("archiveTargets")]
    public Dictionary<string, string> ArchiveTargets
    {
        get => _archiveTargets;
        init => _archiveTargets = value ?? new(StringComparer.OrdinalIgnoreCase);
    }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("excludedPaths")]
    public IReadOnlyList<string> ExcludedPaths
    {
        get => _excludedPaths;
        init => _excludedPaths = value ?? Array.Empty<string>();
    }

    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>
    /// %USERPROFILE% of the backup machine. Restore rewrites paths from this
    /// root to the target profile; it works for profiles outside C:\Users
    /// and across drive letters. Null in backups made before it was added,
    /// where restore infers it from the .claude / .codex source path.
    /// </summary>
    [JsonPropertyName("userProfile")]
    public string? UserProfile { get; init; }

    /// <summary>
    /// Source groups the user selected for this backup (see SourceGroups),
    /// e.g. ["codex"]. Null for a full backup, which is also what every
    /// backup made before selections existed was. Retention keeps each
    /// selection separately so partial backups never prune full ones.
    /// </summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>
    /// Junctions / directory symlinks found inside a source. Their content is
    /// not archived (it lives elsewhere and may be huge or cyclic); restore
    /// recreates the link when its target exists on the restore machine.
    /// </summary>
    [JsonPropertyName("links")]
    public IReadOnlyList<BackupLink> Links
    {
        get => _links;
        init => _links = value ?? Array.Empty<BackupLink>();
    }
}

/// <param name="Path">Archive path of the link itself, e.g. "codex/dotcodex/skills/my-skill".</param>
/// <param name="Target">Link target as stored on disk (absolute, or relative for symlinks).</param>
public sealed record BackupLink(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("target")] string Target);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BackupManifest))]
public partial class BackupManifestJsonContext : JsonSerializerContext
{
}
