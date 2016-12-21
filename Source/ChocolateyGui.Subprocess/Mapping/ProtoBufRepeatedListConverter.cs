using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using Google.Protobuf.Collections;

namespace ChocolateyGui.Subprocess.Mapping
{
    public class ProtoBufRepeatedToListConverter<T> : ITypeConverter<RepeatedField<T>, IList<T>>
    {
        public IList<T> Convert(RepeatedField<T> source, IList<T> destination, ResolutionContext context)
        {
            return source.ToList();
        }
    }

    public class ProtoBufRepeatedToArrayConverter<T> : ITypeConverter<RepeatedField<T>, T[]>
    {
        public T[] Convert(RepeatedField<T> source, T[] destination, ResolutionContext context)
        {
            return source.ToArray();
        }
    }
}
