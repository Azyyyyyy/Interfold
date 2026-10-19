using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Domain.Abstractions.Repository;

public readonly record struct SettingsFieldInsert(
    string Name,
    FieldType Type,
    VisibilityLevel SecurityLevel,
    bool Locked,
    DateTime InsertedAtUtc);

public interface ISettingsFieldRepository
{
    Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<FieldId?> CreateAsync(
        SystemId systemId,
        string name,
        FieldType type,
        VisibilityLevel securityLevel,
        bool locked,
        DateTime insertedAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FieldId>> CreateManyAsync(
        SystemId systemId,
        IReadOnlyList<SettingsFieldInsert> fields,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        SystemId systemId,
        FieldId fieldId,
        string? name,
        VisibilityLevel? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(SystemId systemId, FieldId fieldId, CancellationToken cancellationToken = default);

    Task<bool> RelocateAsync(SystemId systemId, FieldId fieldId, int index, CancellationToken cancellationToken = default);
}
