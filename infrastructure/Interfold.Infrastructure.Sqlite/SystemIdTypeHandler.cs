using System.Data;
using Dapper;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Sqlite;

internal sealed class SystemIdTypeHandler : SqlMapper.TypeHandler<SystemId>
{
    public override void SetValue(IDbDataParameter parameter, SystemId value)
    {
        parameter.Value = value.Value;
    }

    public override SystemId Parse(object value)
    {
        return new SystemId((string)value);
    }
}
