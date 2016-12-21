using System;
using Google.Protobuf.WellKnownTypes;

// ReSharper disable once CheckNamespace
namespace ChocolateyGui.Rpc
{
    // ReSharper disable once StyleCop.SA1601
    public partial class Package
    {
        public DateTimeOffset Published
        {
            get { return PublishedEncoded.ToDateTimeOffset(); }
            set { PublishedEncoded = Timestamp.FromDateTimeOffset(value); }
        }
    }
}
