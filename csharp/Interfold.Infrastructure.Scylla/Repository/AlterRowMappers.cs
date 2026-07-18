using System.Collections.Generic;
using System.Linq;
using Cassandra;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain;
using Interfold.Domain.Alters;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.Scylla.Fixups;

namespace Interfold.Infrastructure.Scylla.Repository;

internal static class AlterRowMappers
{
    public static BareAlter MapBareAlter(Row row, IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        return new BareAlter(
            new(row.GetValue<short>("id")),
            row.GetValue<string>("name"),
            AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
            row.GetValue<short?>("avatar_source").FromCodeOrNull<AvatarSource>(),
            HexColor.FromNullable(row.GetValue<string?>("color")),
            row.GetValue<string?>("pronouns"),
            row.GetValue<string?>("description"),
            AlterFieldProjection.ResolveGuardedFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields")?.ToDictionary(x => new FieldId(x.Id), x => x.Value), definitions));
    }
}
