using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("MERIDIAN_TEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set MERIDIAN_TEST_CONNECTION_STRING to a real PostgreSQL database.");
await using var dataSource = NpgsqlDataSource.Create(connectionString);
await using var connection = await dataSource.OpenConnectionAsync();

var migrationCount = await ScalarLong("SELECT count(*) FROM schema_migrations WHERE version = '001_initial'");
var countryCount = await ScalarLong("SELECT count(*) FROM member_states");
var periodCount = await ScalarLong("SELECT count(*) FROM membership_periods");
if (migrationCount != 1 || countryCount < 100 || periodCount < countryCount)
    throw new InvalidOperationException($"Expected the initial migration and seeded membership data; got migration={migrationCount}, countries={countryCount}, periods={periodCount}.");

await using var sampleCommand = new NpgsqlCommand("SELECT country_code, member_from FROM membership_periods WHERE member_to IS NULL ORDER BY country_code LIMIT 1", connection);
await using var sample = await sampleCommand.ExecuteReaderAsync();
if (!await sample.ReadAsync()) throw new InvalidOperationException("The seed has no active membership period to test.");
var code = sample.GetString(0).Trim();
var start = sample.GetDateTime(1).AddDays(1);
await sample.CloseAsync();
await using var transaction = await connection.BeginTransactionAsync();
try
{
    await using var insert = new NpgsqlCommand("INSERT INTO membership_periods(country_code,member_from) VALUES (@code,@start)", connection, transaction);
    insert.Parameters.AddWithValue("code", code);
    insert.Parameters.AddWithValue("start", start);
    await insert.ExecuteNonQueryAsync();
    throw new InvalidOperationException("The database accepted overlapping membership periods.");
}
catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
{
    Console.WriteLine("PASS: PostgreSQL migration, member seed, and non-overlapping membership constraint.");
}
async Task<long> ScalarLong(string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    return (long)(await command.ExecuteScalarAsync() ?? 0L);
}

