using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using static StrmAssistant.Common.CommonUtility;

namespace StrmAssistant.Options
{
    public enum RefreshPersonOption
    {
        Default,
        FullRefresh,
        NoAdult
    }

    public class MetadataEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_MetadataEnhanceOptions_Metadata_Enhance;

        [DisplayNameL("ModOptions_ChineseMovieDb_Chinese_MovieDb", typeof(Resources))]
        [DescriptionL("ModOptions_ChineseMovieDb_Optimize_MovieDb_for_Chinese_metadata__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool ChineseMovieDb { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> LanguageList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("ModOptions_FallbackLanguages_Fallback_Languages", typeof(Resources))]
        [DescriptionL("ModOptions_FallbackLanguages_Fallback_languages__Default_is_zh_SG_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LanguageList))]
        [VisibleCondition(nameof(ChineseMovieDb), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string FallbackLanguages { get; set; } = "zh-sg";

        [DisplayNameL("MetadataEnhanceOptions_ChineseTvdb_Customize_Tvdb_Fallback_Language", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_ChineseTvdb_Try_to_get_Chinese_or_Japanese_metadata_from_Tvdb__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsTvdbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool ChineseTvdb { get; set; } = false;
        
        [Browsable(false)]
        public List<EditorSelectOption> TvdbLanguageList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("ModOptions_FallbackLanguages_Fallback_Languages", typeof(Resources))]
        [DescriptionL("ModOptions_FallbackLanguages_Fallback_languages__Default_is_zh_SG_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(TvdbLanguageList))]
        [VisibleCondition(nameof(ChineseTvdb), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsTvdbPluginLoaded), SimpleCondition.IsTrue)]
        public string TvdbFallbackLanguages { get; set; } = "zhtw,yue";

        [DisplayNameL("MetadataEnhanceOptions_BlockNonFallbackLanguage_Block_Non_Fallback_Language", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_BlockNonFallbackLanguage_Block_English_such_non_fallback_language_in_overview__Default_is_OFF_", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(ShowBlockNonFallbackLanguage), SimpleCondition.IsTrue)]
        public bool BlockNonFallbackLanguage { get; set; } = false;

        [Browsable(false)]
        public bool ShowBlockNonFallbackLanguage =>
            ChineseMovieDb && IsMovieDbPluginLoaded || ChineseTvdb && IsTvdbPluginLoaded;

        [DisplayNameL("MetadataEnhanceOptions_MovieDbEpisodeGroup_Support_MovieDb_Episode_Group", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MovieDbEpisodeGroup_Support_MovieDb_episode_group_scrapping_for_TV_shows__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool MovieDbEpisodeGroup { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_LocalEpisodeGroup_Local_Episode_Group", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_LocalEpisodeGroup_Store_or_load_episode_group_info_to_from_JSON_file__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(MovieDbEpisodeGroup), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public bool LocalEpisodeGroup { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_EnhanceMovieDbPerson_Enhance_MovieDb_Person", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnhanceMovieDbPerson_Import_season_cast_and_update_series_people__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool EnhanceMovieDbPerson { get; set; } = false;
        
        [Browsable(false)]
        [Required]
        public string RefreshPersonMode { get; set; } = RefreshPersonOption.Default.ToString();

        [DisplayNameL("ModOptions_OriginalPoster_Original_Poster", typeof(Resources))]
        [DescriptionL("ModOptions_OriginalPoster_Show_original_poster_based_on_original_language__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool PreferOriginalPoster { get; set; } = false;

        [DisplayNameL("ModOptions_PinyinSortName_Pinyin_Sort_Title", typeof(Resources))]
        [DescriptionL("ModOptions_PinyinSortName_Auto_generate_pinyin_initials_as_sort_title", typeof(Resources))]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool PinyinSortName { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_EnhanceNfoMetadata_Nfo_Metadata_Import_Enhanced", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnhanceNfoMetadata_Add_support_to_import_actor_image_url__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsNfoMetadataPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool EnhanceNfoMetadata { get; set; } = false;
        
        public enum EpisodeRefreshOption
        {
            [DescriptionL("EpisodeRefreshOption_NoOverview_No_Overview", typeof(Resources))]
            NoOverview,
            [DescriptionL("EpisodeRefreshOption_NoImage_No_Image", typeof(Resources))]
            NoImage,
            [DescriptionL("EpisodeRefreshOption_NonChineseOverview_Non_Chinese_Overview", typeof(Resources))]
            NonChineseOverview,
            [DescriptionL("EpisodeRefreshOption_DefaultEpisodeName_Default_Episode_Name", typeof(Resources))]
            DefaultEpisodeName,
            [DescriptionL("EpisodeRefreshOption_ReplaceCapturedImage_Replace_Captured_Image", typeof(Resources))]
            ReplaceCapturedImage
        }

        [Browsable(false)]
        public List<EditorSelectOption> EpisodeRefreshOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_Metadata_Refresh_Scope", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_refresh_scope_for_scheduled_task_and_catch_up__Default_is_no_overview_and_no_image_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(EpisodeRefreshOptionList))]
        public string EpisodeRefreshScope { get; set; } = string.Join(",", EpisodeRefreshOption.NoOverview.ToString(),
            EpisodeRefreshOption.NoImage.ToString());
        
        [Browsable(false)]
        [Required]
        public int EpisodeRefreshLookBackDays { get; set; } = 365;

        [DisplayNameL("MetadataEnhanceOptions_EnableAltMovieDbUrl", typeof(Resources))]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool AltMovieDbConfig { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbApiUrl_Alternative_MovieDb_Api_Url", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbApiUrl_Default_alternative_is_https___api_tmdb_org", typeof(Resources))]
        [VisibleCondition(nameof(AltMovieDbConfig), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string AltMovieDbApiUrl { get; set; } = "https://api.tmdb.org";

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbImageUrl_Alternative_MovieDb_Image_Url", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbImageUrl_No_default_alternative__Provide_by_yourself_", typeof(Resources))]
        [VisibleCondition(nameof(AltMovieDbConfig), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string AltMovieDbImageUrl { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbApiKey_Alternative_MovieDb_Api_Key", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbApiKey_Provide_your_own_MovieDb_Api_Key__Blank_uses_system_default_", typeof(Resources))]
        [VisibleCondition(nameof(AltMovieDbConfig), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string AltMovieDbApiKey { get; set; } = string.Empty;

        [Browsable(false)]
        public bool IsMovieDbPluginLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MovieDb") && IsModSupported;

        [Browsable(false)]
        public bool IsTvdbPluginLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Tvdb") && IsModSupported;

        [Browsable(false)]
        public bool IsNfoMetadataPluginLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "NfoMetadata") && IsModSupported;

        [Browsable(false)]
        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

        public void Initialize()
        {
            LanguageList.Clear();

            foreach (var language in LanguageUtility.MovieDbFallbackLanguages)
            {
                LanguageList.Add(new EditorSelectOption
                {
                    Value = language.ToLowerInvariant(),
                    Name = language,
                    IsEnabled = true
                });
            }

            TvdbLanguageList.Clear();

            foreach (var language in LanguageUtility.TvdbFallbackLanguages)
            {
                TvdbLanguageList.Add(new EditorSelectOption
                {
                    Value = language,
                    Name = language,
                    IsEnabled = true
                });
            }

            EpisodeRefreshOptionList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(EpisodeRefreshOption)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                EpisodeRefreshOptionList.Add(selectOption);
            }
        }

        protected override void Validate(ValidationContext context)
        {
            string metadataOptionsErrors = null;

            foreach (var (value, isValid, errorResource) in new (string, Func<string, bool>, string)[]
                     {
                         (AltMovieDbApiUrl, IsValidHttpUrl,
                             Resources.InvalidAltMovieDbApiUrl),
                         (AltMovieDbImageUrl, IsValidHttpUrl,
                             Resources.InvalidAltMovieDbImageUrl),
                         (AltMovieDbApiKey, IsValidMovieDbApiKey,
                             Resources.InvalidAltMovieDbApiKey)
                     })
            {
                if (!string.IsNullOrWhiteSpace(value) && !isValid(value))
                {
                    metadataOptionsErrors = metadataOptionsErrors == null ? errorResource : $"{metadataOptionsErrors}; {errorResource}";
                }
            }

            if (!string.IsNullOrEmpty(metadataOptionsErrors))
            {
                context.AddValidationError(metadataOptionsErrors);
            }
        }
    }
}
