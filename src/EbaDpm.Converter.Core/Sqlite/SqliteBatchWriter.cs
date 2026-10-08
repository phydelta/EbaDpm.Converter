using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Sqlite;

/// <summary>
/// Batch writer for the SQLite target tables: one prepared <c>INSERT</c> statement per
/// instance, reusing the same <see cref="SqliteParameter"/> objects row after row, and
/// grouping inserts into transactions of bounded size (<see cref="DefaultBatchSize"/> rows by
/// default).
///
/// An instance writes to a single table. Because SQLite allows only one active transaction per
/// connection, no two <see cref="SqliteBatchWriter"/> instances may be alive at the same time on
/// the same <see cref="SqliteConnection"/>: complete (or dispose) one before opening the next.
/// </summary>
public sealed class SqliteBatchWriter : IDisposable
{
    public const int DefaultBatchSize = 10_000;

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _command;
    private readonly SqliteParameter[] _parameters;
    private readonly int _batchSize;
    private SqliteTransaction? _transaction;
    private int _pendingInBatch;
    private bool _completed;

    public SqliteBatchWriter(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<string> columnNames,
        int batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (columnNames is null || columnNames.Count == 0)
        {
            throw new ArgumentException("At least one column is required.", nameof(columnNames));
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "The batch size must be positive.");
        }

        _connection = connection;
        _batchSize = batchSize;

        // Column names in double quotes: some target columns coincide with an SQL reserved word
        // (e.g. mHierarchyNode.Order). Parameters use positional names ($p0, $p1...), not names
        // derived from the column name, for the same reason.
        var columnList = string.Join(", ", columnNames.Select(QuoteIdentifier));
        var parameterNames = Enumerable.Range(0, columnNames.Count).Select(i => "$p" + i).ToArray();
        var parameterList = string.Join(", ", parameterNames);

        _command = connection.CreateCommand();
        _command.CommandText = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList});";

        _parameters = new SqliteParameter[columnNames.Count];
        for (var i = 0; i < columnNames.Count; i++)
        {
            var parameter = _command.CreateParameter();
            parameter.ParameterName = parameterNames[i];
            _command.Parameters.Add(parameter);
            _parameters[i] = parameter;
        }
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Total rows written so far (includes the batch in progress).</summary>
    public long RowsWritten { get; private set; }

    /// <summary>
    /// Adds a row. Values must be in the same order as the <c>columnNames</c> passed to the
    /// constructor. <see langword="null"/> is translated to <see cref="DBNull.Value"/>.
    /// </summary>
    public void AddRow(params object?[] values)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        if (values.Length != _parameters.Length)
        {
            throw new ArgumentException(
                $"Expected {_parameters.Length} values but received {values.Length}.",
                nameof(values));
        }

        EnsureTransaction();

        for (var i = 0; i < values.Length; i++)
        {
            _parameters[i].Value = values[i] ?? DBNull.Value;
        }

        _command.ExecuteNonQuery();
        RowsWritten++;
        _pendingInBatch++;

        if (_pendingInBatch >= _batchSize)
        {
            CommitBatch();
        }
    }

    /// <summary>
    /// Commits the pending batch, if any, and leaves the writer ready for reuse (it does not
    /// invalidate the instance). Called automatically from <see cref="Dispose"/>.
    /// </summary>
    public void Flush()
    {
        if (_pendingInBatch > 0)
        {
            CommitBatch();
        }
    }

    /// <summary>Commits the pending batch and closes the writer.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        Flush();
        _completed = true;
    }

    private void EnsureTransaction()
    {
        if (_transaction is null)
        {
            _transaction = _connection.BeginTransaction();
            _command.Transaction = _transaction;
        }
    }

    private void CommitBatch()
    {
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
        _pendingInBatch = 0;
    }

    public void Dispose()
    {
        Complete();
        _command.Dispose();
    }
}
