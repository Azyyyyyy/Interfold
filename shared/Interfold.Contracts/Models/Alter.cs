using System;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models;

// AlterPublicFieldReadModel moved to shared/Interfold.Contracts/Models/AlterPublicFieldReadModel.cs
// during the Phase-3 Alters slice pre-move split (stays on spine to avoid a
// Friendships.Contracts -> Alters.Contracts cross-feature reference).
//
// AlterReadModel moved to shared/Interfold.Alters.Contracts/Models/Alter.cs during the Phase-3
// Alters slice. BareAlter stays on the spine because two spine read models bind it
// (TagPublicReadModel.Alters, FrontActiveReadModel.Alter) and neither Tags.Contracts nor
// Fronting.Contracts can back-ref Interfold.Api / Interfold.Alters.Contracts. Dissolves when the
// Post-Alters follow-up lifts TagReadModel + FrontReadModels into their feature Contracts (which
// legalises the cross-feature refs to Alters.Contracts and lets BareAlter migrate too).

public class BareAlter : IAvatarBearing {
    public static BareAlter CreatePlaceholder(AlterId id) => new(id, $"Alter {id}", null, null, null, null, null, Array.Empty<AlterPublicFieldReadModel>());
    public BareAlter(
        AlterId id,
        string name,
        AvatarUrl? avatarUrl,
        AvatarSource? avatarSource,
        HexColor? color,
        string? pronouns,
        string? description,
        IReadOnlyList<AlterPublicFieldReadModel> fields)
    {
        Id = id;
        Name = name;
        AvatarUrl = avatarUrl;
        AvatarSource = avatarSource;
        Color = color;
        Pronouns = pronouns;
        Fields = fields;
        Description = description;
    }

    public AlterId Id { get; set; }
    public AvatarUrl? AvatarUrl { get; set; }
    public AvatarSource? AvatarSource { get; set; }
    public HexColor? Color { get; set; }
    public string Name { get; set; }
    public string? Pronouns { get; set; }
    public IReadOnlyList<AlterPublicFieldReadModel> Fields { get; set; }
    public string? Description { get; set; }
 }
