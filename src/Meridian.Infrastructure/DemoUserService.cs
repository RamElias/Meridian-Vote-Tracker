using Npgsql;

namespace Meridian.Infrastructure;

public sealed record DemoUser(string UserId, string DisplayName, string Role);

public sealed class DemoUserService(NpgsqlDataSource dataSource)
{
    public async Task<DemoUser?> FindAsync(string userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT user_id,display_name,role FROM app_users WHERE user_id=@id");
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new DemoUser(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }
}
