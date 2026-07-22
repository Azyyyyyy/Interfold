using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models;

public sealed class AlterReadModel : BareAlter {

    public AlterReadModel(
        AlterId id,
        string name,
        string? description,
        AvatarUrl? avatarUrl,
        AvatarSource? avatarSource,
        HexColor? color,
        string? pronouns,
        VisibilityLevel securityLevel,
        IReadOnlyList<AlterPublicFieldReadModel> fields,
        string? proxyName,
        string? alias,
        bool? untracked,
        bool? archived,
        bool? pinned) : base(id, name, avatarUrl, avatarSource, color, pronouns, description, fields)
    {
        Alias = alias;
        SecurityLevel = securityLevel;
        ProxyName = proxyName;
        Untracked = untracked ?? false;
        Archived = archived ?? false;
        Pinned = pinned ?? false;
    }

    // The 14-arg ctor above takes `bool?` for the flag trio so the CQL projections in
    // ScyllaAlterRepository can bind `row.GetValue<bool?>(...)` directly. STJ's deserialiser
    // refuses that ctor because `bool? untracked` doesn't type-match the `bool Untracked`
    // property (STJ requires ctor-param type ↔ property-type equality, case-insensitive name
    // match alone isn't enough). That refusal breaks any call-site — production or test — that
    // needs to round-trip an AlterReadModel through JSON. This second ctor accepts the flags
    // as plain `bool`, marked [JsonConstructor] so STJ picks it and never sees the `bool?`
    // form, and forwards to the original so the ??-false coercion stays in one place.
    [JsonConstructor]
    public AlterReadModel(
        AlterId id,
        string name,
        string? description,
        AvatarUrl? avatarUrl,
        AvatarSource? avatarSource,
        HexColor? color,
        string? pronouns,
        VisibilityLevel securityLevel,
        IReadOnlyList<AlterPublicFieldReadModel> fields,
        string? proxyName,
        string? alias,
        bool untracked,
        bool archived,
        bool pinned)
        : this(id, name, description, avatarUrl, avatarSource, color, pronouns, securityLevel, fields, proxyName, alias, (bool?)untracked, (bool?)archived, (bool?)pinned)
    {
    }

    public string? Alias { get; set; }
    public VisibilityLevel SecurityLevel { get; set; }
    public string? ProxyName { get; set; }
    public bool Untracked { get; set; }
    public bool Archived { get; set; }
    public bool Pinned { get; set; }
}
