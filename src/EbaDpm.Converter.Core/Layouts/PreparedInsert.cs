using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>
/// A prepared <c>INSERT</c> reused row by row, like <c>SqliteBatchWriter</c> — but WITHOUT
/// managing its own transaction, because the layout extractor needs several tables open at the
/// same time (declaration, value, ordinate, raw...) and SQLite only allows one active
/// transaction per connection. The transaction is opened and committed by <c>LayoutExtractor</c>.
/// </summary>
internal sealed class PreparedInsert : IDisposable
{
    private readonly SqliteCommand _command;
    private readonly SqliteParameter[] _parameters;

    public PreparedInsert(SqliteConnection connection, string tableName, IReadOnlyList<string> columnNames)
    {
        var columnList = string.Join(", ", columnNames.Select(c => "\"" + c + "\""));
        var parameterNames = Enumerable.Range(0, columnNames.Count).Select(i => "$p" + i).ToArray();
        _command = connection.CreateCommand();
        _command.CommandText = $"INSERT INTO {tableName} ({columnList}) VALUES ({string.Join(", ", parameterNames)});";
        _parameters = new SqliteParameter[columnNames.Count];
        for (var i = 0; i < columnNames.Count; i++)
        {
            var parameter = _command.CreateParameter();
            parameter.ParameterName = parameterNames[i];
            _command.Parameters.Add(parameter);
            _parameters[i] = parameter;
        }
    }

    public SqliteTransaction? Transaction
    {
        set => _command.Transaction = value;
    }

    public void Insert(params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            _parameters[i].Value = values[i] ?? DBNull.Value;
        }

        _command.ExecuteNonQuery();
    }

    public void Dispose() => _command.Dispose();
}
