using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using chocolatey;
using chocolatey.infrastructure.results;

namespace ChocolateyGui.Subprocess
{
    public static class ChocolateyExtensions
    {
        public static Task RunAsync(this GetChocolatey chocolatey, CancellationToken token)
        {
            return Task.Run(() => chocolatey.Run(), token);
        }

        public static Task<ICollection<T>> ListAsync<T>(this GetChocolatey chocolatey, CancellationToken token)
        {
            return Task.Run(() => (ICollection<T>)chocolatey.List<T>().ToList(), token);
        }

        public static Task<ICollection<PackageResult>> ListPackagesAsync(this GetChocolatey chocolatey, CancellationToken token)
        {
            return Task.Run(() => (ICollection<PackageResult>)chocolatey.List<PackageResult>().ToList(), token);
        }
    }
}
