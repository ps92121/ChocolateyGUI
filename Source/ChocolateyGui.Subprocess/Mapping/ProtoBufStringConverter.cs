using AutoMapper;

namespace ChocolateyGui.Subprocess.Mapping
{
    public class ProtoBufStringConverter : ITypeConverter<string, string>
    {
        public string Convert(string source, string destination, ResolutionContext context)
        {
            return source ?? "";
        }
    }
}
