using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    public class MediaInfoExtractOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_EditorTitle_Strm_Extract", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Extract;

        [DisplayNameL("PluginOptions_IncludeExtra_Include_Extra", typeof(Resources))]
        [DescriptionL("PluginOptions_IncludeExtra_Include_media_extras_to_extract__Default_is_False_", typeof(Resources))]
        [Required]
        public bool IncludeExtra { get; set; } = false;

        [DisplayNameL("PluginOptions_EnableImageCapture_Enable_Image_Capture", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableImageCapture_Perform_image_capture_for_videos_without_primary_image__Default_is_False_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool EnableImageCapture { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_ImageCaptureOffset_Image_Capture_Offset", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ImageCaptureOffset_Image_capture_position_as_a_percentage_of_runtime__Default_is_10_", typeof(Resources))]
        [Required, MinValue(10), MaxValue(90)]
        [VisibleCondition(nameof(EnableImageCapture), SimpleCondition.IsTrue)]
        public int ImageCapturePosition { get; set; } = 10;

        [Browsable(false)]
        [Required]
        public string ImageCaptureExcludeMediaContainers { get; set; } =
            string.Join(",", new[] { MediaContainers.MpegTs, MediaContainers.Ts, MediaContainers.M2Ts });

        [DisplayNameL("ModOptions_ExclusiveExtract_Exclusive_Extract", typeof(Resources))]
        [DescriptionL("ModOptions_ExclusiveExtract_Only_allow_this_plugin_to_extract_media_info__ffprobe__and_capture_image__ffmpeg___Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool ExclusiveExtract { get; set; } = false;

        public enum ExclusiveControl
        {
            [DescriptionL("ExclusiveControl_IgnoreFileChange_IgnoreFileChange", typeof(Resources))]
            IgnoreFileChange,
            [DescriptionL("ExclusiveControl_ExtractOnFileChange_ExtractOnFileChange", typeof(Resources))]
            ExtractOnFileChange,
            [DescriptionL("ExclusiveControl_CatchAllAllow_CatchAllAllow", typeof(Resources))]
            CatchAllAllow,
            [DescriptionL("ExclusiveControl_CatchAllBlock_CatchAllBlock", typeof(Resources))]
            CatchAllBlock,
            [DescriptionL("ExclusiveControl_IgnoreExtSubChange_IgnoreExtSubChange", typeof(Resources))]
            IgnoreExtSubChange,
            [DescriptionL("ExclusiveControl_NoPersistIntegration_NoPersistIntegration", typeof(Resources))]
            NoPersistIntegration,
            [DescriptionL("ExclusiveControl_NoIntroProtect_NoIntroProtect", typeof(Resources))]
            NoIntroProtect,
            [DescriptionL("ExclusiveControl_NoNfoSaverOptimization_No_Nfo_Saver_Optimization", typeof(Resources))]
            NoNfoSaverOptimization,
            [DescriptionL("ExclusiveControl_ExtractAlternative_ExtractAlternative", typeof(Resources))]
            ExtractAlternative
        }

        [Browsable(false)]
        public List<EditorSelectOption> ExclusiveControlList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MediaInfoExtractOptions_ExclusiveExtractFeatureControl_Exclusive_Extract_Feature_Control", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(ExclusiveControlList))]
        [VisibleCondition(nameof(ExclusiveExtract), SimpleCondition.IsTrue)]
        public string ExclusiveControlFeatures { get; set; } = string.Empty;

        public enum PersistMediaInfoOption
        {
            None,
            Default,
            Restore
        }

        [Browsable(false)]
        public List<EditorRadioOption> PersistMediaInfoOptionList { get; set; } = new List<EditorRadioOption>();

        [DisplayName("")]
        [SelectItemsSource(nameof(PersistMediaInfoOptionList))]
        [SelectShowRadioGroup]
        public string PersistMediaInfoMode { get; set; } = PersistMediaInfoOption.None.ToString();

        [DisplayNameL("MediaInfoExtractOptions_MediaInfoJsonRootFolder_MediaInfo_Json_Root_Folder", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_MediaInfoJsonRootFolder_Store_or_load_media_info_JSON_files_under_this_root_folder__Default_is_EMPTY_", typeof(Resources))]
        [EditFolderPicker]
        [VisibleCondition(nameof(PersistMediaInfoMode), ValueCondition.IsNotEqual, PersistMediaInfoOption.None)]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;

        [Browsable(false)]
        public List<EditorSelectOption> LibraryList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("PluginOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("PluginOptions_LibraryScope_Library_scope_to_extract__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string LibraryScope { get; set; } = string.Empty;

        [Browsable(false)]
        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

        public void Initialize(ILibraryManager libraryManager)
        {
            LibraryList.Clear();

            LibraryList.Add(new EditorSelectOption
            {
                Value = "-1",
                Name = Resources.Favorites,
                IsEnabled = true
            });

            var libraries = libraryManager.GetVirtualFolders();

            foreach (var item in libraries)
            {
                if (LibraryApi.ExcludedCollectionTypes.Contains(item.CollectionType))
                {
                    continue;
                }

                var selectOption = new EditorSelectOption
                {
                    Value = item.ItemId,
                    Name = item.Name,
                    IsEnabled = true,
                };

                LibraryList.Add(selectOption);
            }

            ExclusiveControlList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(ExclusiveControl)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                ExclusiveControlList.Add(selectOption);
            }

            PersistMediaInfoOptionList.Clear();
            PersistMediaInfoOptionList = new List<EditorRadioOption>
            {
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.Default,
                    PrimaryText = Resources.MediaInfoExtractOptions_PersistMediaInfo_Persist_MediaInfo,
                    SecondaryText =
                        Resources
                            .MediaInfoExtractOptions_PersistMediaInfo_Persist_media_info_in_JSON_file__Default_is_OFF_
                },
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.Restore,
                    PrimaryText = Resources.MediaInfoExtractOptions_MediaInfoRestoreMode_MediaInfo_Restore_Mode,
                    SecondaryText =
                        Resources
                            .MediaInfoExtractOptions_MediaInfoRestoreMode_Only_restore_media_info__chapters__and_video_thumbnails_from_JSON_or_BIF__skipping_extraction__Default_is_OFF_
                },
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.None,
                    PrimaryText = Resources.PersistMediaInfoOption_None_None
                }
            };
        }
    }
}
