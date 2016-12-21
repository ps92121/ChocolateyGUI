using System;
using AutoMapper;

namespace ChocolateyGui.Subprocess.Mapping
{
    public class UriToStringConverter : ITypeConverter<Uri, string>
    {
        public string Convert(Uri source, string destination, ResolutionContext context)
        {
            return source?.ToString() ?? "";
        }
    }
}
