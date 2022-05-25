﻿using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using ReactiveUI;
using System.Windows;
using System.Windows.Forms;
using DynamicData;
using Microsoft.WindowsAPICodePack.Dialogs;
using Wabbajack.Common;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.View_Models.Controls;

namespace Wabbajack
{
    /// <summary>
    /// Interaction logic for CompilerView.xaml
    /// </summary>
    public partial class CompilerView : ReactiveUserControl<CompilerVM>
    {
        public CompilerView()
        {
            InitializeComponent();

            this.WhenActivated(disposables =>
            {

                
                ViewModel.WhenAnyValue(vm => vm.ExecuteCommand)
                    .BindToStrict(this, view => view.BeginButton.Command)
                    .DisposeWith(disposables);
                
                ViewModel.WhenAnyValue(vm => vm.BackCommand)
                    .BindToStrict(this, view => view.BackButton.Command)
                    .DisposeWith(disposables);
                
                ViewModel.WhenAnyValue(vm => vm.State)
                    .Select(v => v == CompilerState.Configuration ? Visibility.Visible : Visibility.Collapsed)
                    .BindToStrict(this, view => view.BottomCompilerSettingsGrid.Visibility)
                    .DisposeWith(disposables);

                ViewModel.WhenAnyValue(vm => vm.State)
                    .Select(v => v != CompilerState.Configuration ? Visibility.Visible : Visibility.Collapsed)
                    .BindToStrict(this, view => view.LogView.Visibility)
                    .DisposeWith(disposables);

                ViewModel.WhenAnyValue(vm => vm.State)
                    .Select(v => v == CompilerState.Compiling ? Visibility.Visible : Visibility.Collapsed)
                    .BindToStrict(this, view => view.CpuView.Visibility)
                    .DisposeWith(disposables);
                
                ViewModel.WhenAnyValue(vm => vm.State)
                    .Select(v => v == CompilerState.Completed ? Visibility.Visible : Visibility.Collapsed)
                    .BindToStrict(this, view => view.CompilationComplete.Visibility)
                    .DisposeWith(disposables);
                
                ViewModel.WhenAnyValue(vm => vm.ModlistLocation)
                    .BindToStrict(this, view => view.CompilerConfigView.ModListLocation.PickerVM)
                    .DisposeWith(disposables);
                
                ViewModel.WhenAnyValue(vm => vm.DownloadLocation)
                    .BindToStrict(this, view => view.CompilerConfigView.DownloadsLocation.PickerVM)
                    .DisposeWith(disposables);
                
                ViewModel.WhenAnyValue(vm => vm.OutputLocation)
                    .BindToStrict(this, view => view.CompilerConfigView.OutputLocation.PickerVM)
                    .DisposeWith(disposables);
                
                UserInterventionsControl.Visibility = Visibility.Collapsed;
                
                
                // Settings 
                
                this.Bind(ViewModel, vm => vm.ModListName, view => view.ModListNameSetting.Text)
                    .DisposeWith(disposables);
                
                this.Bind(ViewModel, vm => vm.Author, view => view.AuthorNameSetting.Text)
                    .DisposeWith(disposables);
                
                this.Bind(ViewModel, vm => vm.Version, view => view.VersionSetting.Text)
                    .DisposeWith(disposables);

                this.Bind(ViewModel, vm => vm.Description, view => view.DescriptionSetting.Text)
                    .DisposeWith(disposables);

                
                this.Bind(ViewModel, vm => vm.ModListImagePath, view => view.ImageFilePicker.PickerVM)
                    .DisposeWith(disposables);
                
                this.Bind(ViewModel, vm => vm.Website, view => view.WebsiteSetting.Text)
                    .DisposeWith(disposables);
                
                this.Bind(ViewModel, vm => vm.Readme, view => view.ReadmeSetting.Text)
                    .DisposeWith(disposables);
                
                this.Bind(ViewModel, vm => vm.IsNSFW, view => view.NSFWSetting.IsChecked)
                    .DisposeWith(disposables);
                
                this.Bind(ViewModel, vm => vm.PublishUpdate, view => view.PublishUpdate.IsChecked)
                    .DisposeWith(disposables);

                this.Bind(ViewModel, vm => vm.MachineUrl, view => view.MachineUrl.Text)
                    .DisposeWith(disposables);

                ViewModel.WhenAnyValue(vm => vm.AlwaysEnabled)
                    .WhereNotNull()
                    .Select(itms => itms.Select(itm => new RemovableItemViewModel(itm.ToString(), () => ViewModel.RemoveAlwaysEnabled(itm))).ToArray())
                    .BindToStrict(this, view => view.AlwaysEnabled.ItemsSource)
                    .DisposeWith(disposables);

                AddAlwaysEnabled.Command = ReactiveCommand.CreateFromTask(async () => await AddAlwaysEnabledCommand());
                
                ViewModel.WhenAnyValue(vm => vm.OtherProfiles)
                    .WhereNotNull()
                    .Select(itms => itms.Select(itm => new RemovableItemViewModel(itm.ToString(), () => ViewModel.RemoveProfile(itm))).ToArray())
                    .BindToStrict(this, view => view.OtherProfiles.ItemsSource)
                    .DisposeWith(disposables);

                AddOtherProfile.Command = ReactiveCommand.CreateFromTask(async () => await AddOtherProfileCommand());


            });

        }

        public async Task AddAlwaysEnabledCommand()
        {
            AbsolutePath dirPath;

            if (ViewModel!.Source != default && ViewModel.Source.Combine("mods").DirectoryExists())
            {
                dirPath = ViewModel.Source.Combine("mods");
            }
            else
            {
                dirPath = ViewModel.Source;
            }
            
            var dlg = new CommonOpenFileDialog
            {
                Title = "Please select a folder",
                IsFolderPicker = true,
                InitialDirectory = dirPath.ToString(),
                AddToMostRecentlyUsedList = false,
                AllowNonFileSystemItems = false,
                DefaultDirectory = dirPath.ToString(),
                EnsureFileExists = true,
                EnsurePathExists = true,
                EnsureReadOnly = false,
                EnsureValidNames = true,
                Multiselect = false,
                ShowPlacesList = true,
            };

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok) return;
            var selectedPath = dlg.FileNames.First().ToAbsolutePath();

            if (!selectedPath.InFolder(ViewModel.Source)) return;
            
            ViewModel.AddAlwaysEnabled(selectedPath.RelativeTo(ViewModel.Source));
        }
        
        public async Task AddOtherProfileCommand()
        {
            AbsolutePath dirPath;

            if (ViewModel!.Source != default && ViewModel.Source.Combine("mods").DirectoryExists())
            {
                dirPath = ViewModel.Source.Combine("mods");
            }
            else
            {
                dirPath = ViewModel.Source;
            }
            
            var dlg = new CommonOpenFileDialog
            {
                Title = "Please select a profile folder",
                IsFolderPicker = true,
                InitialDirectory = dirPath.ToString(),
                AddToMostRecentlyUsedList = false,
                AllowNonFileSystemItems = false,
                DefaultDirectory = dirPath.ToString(),
                EnsureFileExists = true,
                EnsurePathExists = true,
                EnsureReadOnly = false,
                EnsureValidNames = true,
                Multiselect = false,
                ShowPlacesList = true,
            };

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok) return;
            var selectedPath = dlg.FileNames.First().ToAbsolutePath();

            if (!selectedPath.InFolder(ViewModel.Source.Combine("profiles"))) return;
            
            ViewModel.AddOtherProfile(selectedPath.FileName.ToString());
        }
    }
}