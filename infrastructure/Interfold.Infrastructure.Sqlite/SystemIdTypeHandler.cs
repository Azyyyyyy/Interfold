using System.Data;
using Dapper;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Sqlite;

internal sealed class SystemIdTypeHandler : SqlMapper.TypeHandler<SystemId>
{
    public override void SetValue(IDbDataParameter parameter, SystemId value)
        => parameter.Value = value.Value;

    public override SystemId Parse(object value)
        => new((string)value);
}

internal sealed class NullableSystemIdTypeHandler : SqlMapper.TypeHandler<SystemId?>
{
    public override void SetValue(IDbDataParameter parameter, SystemId? value)
        => parameter.Value = value is { } id ? id.Value : DBNull.Value;

    public override SystemId? Parse(object value)
        => value is null or DBNull ? null : new SystemId((string)value);
}
