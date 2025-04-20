using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    public class DeletePersonTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        public DeletePersonTask(ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("DeletePerson - Scheduled Task Execute");

            var personItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Person" }
            });

            _logger.Info("DeletePerson - Number of Persons: " + personItems.Length);

            _libraryManager.DeleteItems(personItems.Select(i => i.InternalId).ToArray());

            progress.Report(100.0);
            _logger.Info("DeletePerson - Scheduled Task Complete");
            return Task.CompletedTask;
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "DeletePersonTask";

        public string Description =>
            Resources.ResourceManager.GetString("DeletePersonTask_Description_Deletes_all_persons",
                Plugin.Instance.DefaultUICulture);

        public string Name => "Delete Persons";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => !Plugin.Instance.DebugMode;

        public bool IsEnabled => Plugin.Instance.DebugMode;
        
        public bool IsLogged => true;
    }
}
