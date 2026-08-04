using Remi.Domain;

namespace Remi.Application;

public interface IRemiStore
{
    Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default);

    Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default);
}
