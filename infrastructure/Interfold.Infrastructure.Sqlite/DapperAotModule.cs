using Dapper;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Ids;

[module: DapperAot]
[module: SqlSyntax(SqlSyntax.SQLite)]
[module: TypeHandler(typeof(SystemId), typeof(SystemIdTypeHandler))]
[module: TypeHandler(typeof(SystemId?), typeof(NullableSystemIdTypeHandler))]
