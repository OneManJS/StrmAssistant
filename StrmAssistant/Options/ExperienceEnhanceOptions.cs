using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    public class ExperienceEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_EditorTitle_Experience_Enhance;
        
        [DisplayNameL("GeneralOptions_MergeMultiVersion_Merge_Multiple_Versions", typeof(Resources))]
        [DescriptionL("GeneralOptions_MergeMultiVersion_Auto_merge_multiple_versions_if_in_the_same_folder_", typeof(Resources))]
        [Required]
        public bool MergeMultiVersion { get; set; } = false;

        public enum MergeMoviesScopeOption
        {
            [DescriptionL("MergeScopeOption_FolderScope_FolderScope", typeof(Resources))]
            FolderScope,
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }
        
        [DisplayNameL("ExperienceEnhanceOptions_MergeMoviePreferences_Movie_Merge_Preference", typeof(Resources))]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeMoviesScopeOption MergeMoviesPreference { get; set; } = MergeMoviesScopeOption.FolderScope;
        
        public enum MergeSeriesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [DisplayNameL("ExperienceEnhanceOptions_MergeSeriesPreferences_Series_Merge_Preference", typeof(Resources))]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public MergeSeriesScopeOption MergeSeriesPreference { get; set; } = MergeSeriesScopeOption.LibraryScope;

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public ButtonItem SplitMoviesButton =>
            new ButtonItem(
                Resources.ExperienceEnhanceOptions_SplitMovieButton_Split_multi_version_movies_in_all_libraries)
            {
                Icon = IconNames.clear_all, Data1 = "SplitMovies", ConfirmationPrompt = Resources.AreYouSureToContinue
            };

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public GenericItemList SplitMoviesProgress { get; set; } = new GenericItemList();

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public SpacerItem SplitMovieProgressSeparator { get; set; } = new SpacerItem(SpacerSize.Small);

        [DisplayNameL("ExperienceEnhanceOptions_EnhanceNotification_Enhance_Notification", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnhanceNotification_Show_episode_details_in_series_notification__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool EnhanceNotificationSystem { get; set; } = false;
        
        [DisplayNameL("ExperienceEnhanceOptions_EnableDeepDelete_Enable_Deep_Delete", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnableDeepDelete_Attempt_to_cascade_delete_the_underlying_file_of_strm_or_symlink__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        [Required]
        public bool EnableDeepDelete { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_SuppressPluginUpdates_Suppress_Auto_Plugin_Updates", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_SuppressPluginUpdates_Plugin_names_separated_by_comma_or_semicolon_like_MovieDb_Tvdb__Default_is_BLANK_", typeof(Resources))]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public string SuppressPluginUpdates { get; set; } = string.Empty;

        [DisplayNameL("UIFunctionOptions_EditorTitle_UI_Functions", typeof(Resources))]
        public UIFunctionOptions UIFunctionOptions { get; set; } = new UIFunctionOptions();

        [Browsable(false)]
        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }
}
