using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TickRiver.Core;

public sealed class Store
{
    private readonly string _connectionString;
    public string Path { get; }

    public Store(string path, bool readOnly = false)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!readOnly) Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path, ForeignKeys = true,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        if (readOnly) return;
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS clients (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, currency TEXT NOT NULL,
                rate TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS projects (
                id INTEGER PRIMARY KEY, client_id INTEGER NOT NULL REFERENCES clients(id),
                name TEXT NOT NULL, rate TEXT, billable INTEGER NOT NULL DEFAULT 1, archived INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER NOT NULL REFERENCES projects(id),
                description TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER,
                rate TEXT NOT NULL, currency TEXT NOT NULL, billable INTEGER NOT NULL, invoiced INTEGER NOT NULL DEFAULT 0,
                CHECK (end IS NULL OR end >= start));
            CREATE UNIQUE INDEX IF NOT EXISTS one_running_entry ON entries ((1)) WHERE end IS NULL;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='entries'";
        if (!((string)command.ExecuteScalar()!).Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
        {
            using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE entries_v2 (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER NOT NULL REFERENCES projects(id),
                    description TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER,
                    rate TEXT NOT NULL, currency TEXT NOT NULL, billable INTEGER NOT NULL, invoiced INTEGER NOT NULL DEFAULT 0,
                    CHECK (end IS NULL OR end >= start));
                INSERT INTO entries_v2 SELECT id,project_id,description,start,end,rate,currency,billable,invoiced FROM entries;
                DROP TABLE entries;
                ALTER TABLE entries_v2 RENAME TO entries;
                CREATE UNIQUE INDEX one_running_entry ON entries ((1)) WHERE end IS NULL;
                PRAGMA user_version=2;
                """;
            command.ExecuteNonQuery(); transaction.Commit(); command.Transaction = null;
        }
        else { command.CommandText = "PRAGMA user_version=2"; command.ExecuteNonQuery(); }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public List<Client> Clients()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,currency,rate,archived FROM clients ORDER BY archived,name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var result = new List<Client>();
        while (reader.Read()) result.Add(new Client(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetBoolean(4)));
        return result;
    }

    public List<Project> Projects()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id,p.client_id,p.name,c.name,c.currency,c.rate,p.rate,p.billable,p.archived,c.archived
            FROM projects p JOIN clients c ON c.id=p.client_id ORDER BY p.archived,c.name,p.name COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        var result = new List<Project>();
        while (reader.Read()) result.Add(new Project(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9)));
        return result;
    }

    public List<TimeEntry> Entries(bool runningOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,p.id,c.id,p.name,c.name,e.description,e.start,e.end,e.rate,e.currency,e.billable,e.invoiced
            FROM entries e JOIN projects p ON p.id=e.project_id JOIN clients c ON c.id=p.client_id
            """ + (runningOnly ? " WHERE e.end IS NULL" : "") + " ORDER BY e.start DESC";
        using var reader = command.ExecuteReader();
        var result = new List<TimeEntry>();
        while (reader.Read()) result.Add(new TimeEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
            reader.IsDBNull(7) ? null : new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
            decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture), reader.GetString(9), reader.GetBoolean(10), reader.GetBoolean(11)));
        return result;
    }

    public long LatestEntryId()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id),0) FROM entries";
        return (long)command.ExecuteScalar()!;
    }

    public long SaveClient(long id, string name, string currency, decimal rate, bool archived)
    {
        name = name.Trim(); currency = currency.Trim().ToUpperInvariant();
        if (name.Length == 0) throw new ArgumentException("Enter a client name.");
        if (currency.Length != 3 || currency.Any(c => c < 'A' || c > 'Z')) throw new ArgumentException("Enter a three-letter currency code, such as USD or EUR.");
        if (rate < 0) throw new ArgumentException("The hourly rate cannot be negative.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = id == 0
            ? "INSERT INTO clients(name,currency,rate,archived) VALUES($name,$currency,$rate,$archived); SELECT last_insert_rowid();"
            : "UPDATE clients SET name=$name,currency=$currency,rate=$rate,archived=$archived WHERE id=$id; SELECT $id;";
        Add(command, ("$id", id), ("$name", name), ("$currency", currency), ("$rate", rate.ToString(CultureInfo.InvariantCulture)), ("$archived", archived));
        return (long)command.ExecuteScalar()!;
    }

    public long SaveProject(long id, long clientId, string name, decimal? rate, bool billable, bool archived)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a project name.");
        if (rate < 0) throw new ArgumentException("The hourly rate cannot be negative.");
        if (id != 0 && Projects().Single(p => p.Id == id).ClientId != clientId)
            throw new ArgumentException("An existing project's client cannot be changed. Create a new project for the other client.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = id == 0
            ? "INSERT INTO projects(client_id,name,rate,billable,archived) VALUES($client,$name,$rate,$billable,$archived); SELECT last_insert_rowid();"
            : "UPDATE projects SET name=$name,rate=$rate,billable=$billable,archived=$archived WHERE id=$id; SELECT $id;";
        Add(command, ("$id", id), ("$client", clientId), ("$name", name.Trim()), ("$rate", rate?.ToString(CultureInfo.InvariantCulture)), ("$billable", billable), ("$archived", archived));
        return (long)command.ExecuteScalar()!;
    }

    public long Start(long projectId, string description, DateTimeOffset now)
    {
        var project = Projects().Single(p => p.Id == projectId);
        if (project.Archived || project.ClientArchived) throw new ArgumentException("Unarchive the project and client before starting a timer.");
        if (Entries(runningOnly: true).Count != 0) throw new ArgumentException("Stop the active timer first.");
        return WriteEntry(0, projectId, description, now, null, project.EffectiveRate, project.Currency, project.Billable, false);
    }

    public void Stop(DateTimeOffset now, long? expectedId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Guard the identity atomically: a delayed stop must never stop a replacement timer.
        command.CommandText = "UPDATE entries SET end=$now WHERE end IS NULL AND ($id IS NULL OR id=$id) AND start<=$now";
        Add(command, ("$id", expectedId), ("$now", now.UtcTicks));
        if (command.ExecuteNonQuery() == 0)
        {
            var entry = Entries(runningOnly: true).SingleOrDefault();
            if (entry != null && now < entry.Start) throw new ArgumentException("The system clock is earlier than the timer start. Correct the clock before stopping.");
            throw new ArgumentException("There is no matching active timer. Refresh the current timer before stopping it.");
        }
    }

    public long SaveEntry(long id, long projectId, string description, DateTimeOffset start, DateTimeOffset end,
        decimal rate, bool billable, bool invoiced, TimeEntry? expected = null)
    {
        if (end <= start) throw new ArgumentException("The end must be after the start. For overnight work, change the end date too.");
        if (rate < 0) throw new ArgumentException("The rate cannot be negative.");
        var project = Projects().Single(p => p.Id == projectId);
        var existing = id == 0 ? null : Entries().SingleOrDefault(e => e.Id == id) ?? throw new ArgumentException("This entry was removed. Read it again before editing.");
        if (existing?.End == null && existing != null) throw new ArgumentException("Stop the timer before editing it.");
        // A rate/currency snapshot survives subsequent client/project changes.
        var currency = existing?.ProjectId == projectId ? existing.Currency : project.Currency;
        return WriteEntry(id, projectId, description, start, end, rate, currency, billable, invoiced && billable, expected);
    }

    private long WriteEntry(long id, long projectId, string description, DateTimeOffset start, DateTimeOffset? end,
        decimal rate, string currency, bool billable, bool invoiced, TimeEntry? expected = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM entries WHERE id<>$id AND start<$end AND (end IS NULL OR end>$start)";
        Add(check, ("$id", id), ("$start", start.UtcTicks), ("$end", end?.UtcTicks ?? long.MaxValue));
        if ((long)check.ExecuteScalar()! > 0) throw new ArgumentException("This time overlaps an existing entry. Adjust the times or stop the active timer first.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = id == 0 ? """
            INSERT INTO entries(project_id,description,start,end,rate,currency,billable,invoiced)
            VALUES($project,$description,$start,$end,$rate,$currency,$billable,$invoiced); SELECT last_insert_rowid();
            """ : """
            UPDATE entries SET project_id=$project,description=$description,start=$start,end=$end,
            rate=$rate,currency=$currency,billable=$billable,invoiced=$invoiced WHERE id=$id
            """;
        if (id != 0)
        {
            if (expected != null)
            {
                command.CommandText += " AND project_id=$oldProject AND description=$oldDescription AND start=$oldStart AND end IS $oldEnd AND rate=$oldRate AND currency=$oldCurrency AND billable=$oldBillable AND invoiced=$oldInvoiced";
                Add(command, ("$oldProject", expected.ProjectId), ("$oldDescription", expected.Description), ("$oldStart", expected.Start.UtcTicks),
                    ("$oldEnd", expected.End?.UtcTicks), ("$oldRate", expected.Rate.ToString(CultureInfo.InvariantCulture)), ("$oldCurrency", expected.Currency),
                    ("$oldBillable", expected.Billable), ("$oldInvoiced", expected.Invoiced));
            }
            command.CommandText += " RETURNING id;";
        }
        Add(command, ("$id", id), ("$project", projectId), ("$description", description.Trim()), ("$start", start.UtcTicks),
            ("$end", end?.UtcTicks), ("$rate", rate.ToString(CultureInfo.InvariantCulture)), ("$currency", currency), ("$billable", billable), ("$invoiced", invoiced));
        var result = command.ExecuteScalar() as long? ?? throw new ArgumentException("This entry changed or was removed. Read it again before editing.");
        transaction.Commit();
        return result;
    }

    public void DeleteEntry(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entries WHERE id=$id AND end IS NOT NULL";
        Add(command, ("$id", id)); command.ExecuteNonQuery();
    }

    public void MarkInvoiced(Report report, bool invoiced)
    {
        var groups = report.Rows.Where(r => r.Entry.Billable).GroupBy(r => r.Entry.Id).ToArray();
        if (groups.Any(g => g.Sum(r => r.Seconds) != g.First().Entry.Seconds))
            throw new ArgumentException("Some entries extend beyond this range. Include their full dates before changing invoice status.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var group in groups)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE entries SET invoiced=$value WHERE id=$id";
            Add(command, ("$id", group.Key), ("$value", invoiced)); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Backup(string path)
    {
        if (System.IO.Path.GetFullPath(path).Equals(Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a backup file separate from the live database.");
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target);
    }

    private static void Add(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
    }
}
