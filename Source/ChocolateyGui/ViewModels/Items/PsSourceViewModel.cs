// --------------------------------------------------------------------------------------------------------------------
// <copyright company="Procede Software, LP" file="PsSourceViewModel.cs">
//   Copyright 2017 - Procede Software, LP
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using ChocolateyGui.Base;

namespace ChocolateyGui.ViewModels.Items
{
    public class PsSourceViewModel : ObservableBase
    {
        private string _name;
        private string _url;

        public string Name
        {
            get { return _name; }
            set { SetPropertyValue(ref _name, value); }
        }

        public string Url
        {
            get { return _url; }
            set { SetPropertyValue(ref _url, value); }
        }
    }
}