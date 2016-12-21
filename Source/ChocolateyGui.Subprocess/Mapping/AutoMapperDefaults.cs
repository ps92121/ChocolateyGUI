using System;
using System.Collections.Generic;
using AutoMapper;
using Google.Protobuf.Collections;

namespace ChocolateyGui.Subprocess.Mapping
{
    public static class AutoMapperDefaults
    {
        public static IMapperConfigurationExpression AddDefaults(this IMapperConfigurationExpression config)
        {
            config.CreateMap<string, string>().ConvertUsing<ProtoBufStringConverter>();
            config.CreateMap<Uri, string>().ConvertUsing<UriToStringConverter>();
            config.CreateMap<RepeatedField<string>, IList<string>>().ConvertUsing<ProtoBufRepeatedToListConverter<string>>();
            config.CreateMap<IEnumerable<string>, RepeatedField<string>>().AfterMap((s, d) => d.Add(s)).ConvertUsing((s, d) => d);

            return config;
        }
    }
}
