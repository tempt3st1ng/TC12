#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2023 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)


/*
    13:00 started reviewing https://github.com/ShareX/ShareX/blob/develop/ShareX/WatchFolderManager.cs src


    Bugs:
    Publicly accessible WatchFolders collection.
    Possible NRE: Program.DefaultTaskSettings.WatchFolderList
    Possible NRE: Program.HotkeysConfig.Hotkeys
    Concurrency issue in 'AddWatchFolder' ('if (!IsExist(watchFolderSetting))')
    Concurrency issue in 'AddWatchFolder' (' if (!taskSettings.WatchFolderList.Contains(watchFolderSetting))')
    Memory leak in ' watchFolder.FileWatcherTrigger += origPath =>...'
    Concurrency issue when check and move files
    Concurrency issue in 'RemoveWatchFolder'
    It is possible to use of the disposed object after 'UpdateWatchFolderState' method called
    Is is possible to dispose objects more than once

    Issues:
    Disposable pattern is not used in non sealed class,

    13:30 review is finished, 8 issues found.

*/

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShareX
{
    /// <summary>
    /// Maintains the list of folders to monitor.
    /// <remarks>
    /// !!!! This class is not thread safe !!!!
    /// </remarks>
    /// </summary>
    public class WatchFolderManager : IDisposable
    {
        /// <summary>
        /// Contains the list of the <see cref="WatchFolder"/> objects to watch.
        /// </summary>
        public List<WatchFolder> WatchFolders { get; private set; }

        /// <summary>
        /// Updates the <see cref="WatchFolders"/> collection:
        /// 1. Unregisters current object list
        /// 2. Items taken from the global objects:
        ///    - Program.DefaultTaskSettings.WatchFolderList
        ///    - Program.HotkeysConfig.Hotkeys
        ///    will be added using <see cref="AddWatchFolder"/>
        /// </summary>
        public void UpdateWatchFolders()
        {
            if (WatchFolders != null)
            {
                UnregisterAllWatchFolders();
            }

            WatchFolders = new List<WatchFolder>();

            foreach (WatchFolderSettings defaultWatchFolderSetting in Program.DefaultTaskSettings.WatchFolderList)
            {
                AddWatchFolder(defaultWatchFolderSetting, Program.DefaultTaskSettings);
            }

            foreach (HotkeySettings hotkeySetting in Program.HotkeysConfig.Hotkeys)
            {
                foreach (WatchFolderSettings watchFolderSetting in hotkeySetting.TaskSettings.WatchFolderList)
                {
                    AddWatchFolder(watchFolderSetting, hotkeySetting.TaskSettings);
                }
            }
        }

        private WatchFolder FindWatchFolder(WatchFolderSettings watchFolderSetting)
        {
            return WatchFolders.FirstOrDefault(watchFolder => watchFolder.Settings == watchFolderSetting);
        }

        private bool IsExist(WatchFolderSettings watchFolderSetting)
        {
            return FindWatchFolder(watchFolderSetting) != null;
        }

        /// <summary>
        /// Adds new folder to monitor in the <see cref="WatchFolders"/> collection is not present in the collection.
        /// </summary>
        /// <param name="watchFolderSetting">
        /// <see cref="WatchFolderSettings"/> object.
        /// To use as a <c>Settings</c> for the new <see cref="WatchFolder"/>.
        /// </param>
        /// <param name="taskSettings"><see cref="TaskSettings"/> object.
        /// To use as a <c>TaskSettings</c> for the new <see cref="WatchFolder"/>.</param>
        public void AddWatchFolder(WatchFolderSettings watchFolderSetting, TaskSettings taskSettings)
        {
            if (!IsExist(watchFolderSetting))
            {
                if (!taskSettings.WatchFolderList.Contains(watchFolderSetting))
                {
                    taskSettings.WatchFolderList.Add(watchFolderSetting);
                }

                WatchFolder watchFolder = new WatchFolder();
                watchFolder.Settings = watchFolderSetting;
                watchFolder.TaskSettings = taskSettings;

                watchFolder.FileWatcherTrigger += origPath =>
                {
                    TaskSettings taskSettingsCopy = TaskSettings.GetSafeTaskSettings(taskSettings);
                    string destPath = origPath;

                    if (watchFolderSetting.MoveFilesToScreenshotsFolder)
                    {
                        string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(taskSettingsCopy);
                        string fileName = Path.GetFileName(origPath);
                        destPath = TaskHelpers.HandleExistsFile(screenshotsFolder, fileName, taskSettingsCopy);
                        FileHelpers.CreateDirectoryFromFilePath(destPath);
                        File.Move(origPath, destPath);
                    }

                    UploadManager.UploadFile(destPath, taskSettingsCopy);
                };

                WatchFolders.Add(watchFolder);

                if (taskSettings.WatchFolderEnabled)
                {
                    watchFolder.Enable();
                }
            }
        }

        /// <summary>
        /// Removes <see cref="WatchFolder"/> object from the <see cref="WatchFolders"/> collection.
        /// Search for existing object performed by using <paramref name="watchFolderSetting"/>.
        /// </summary>
        /// <param name="watchFolderSetting"><see cref="WatchFolderSettings"/> object.</param>
        public void RemoveWatchFolder(WatchFolderSettings watchFolderSetting)
        {
            using (WatchFolder watchFolder = FindWatchFolder(watchFolderSetting))
            {
                if (watchFolder != null)
                {
                    watchFolder.TaskSettings.WatchFolderList.Remove(watchFolderSetting);
                    WatchFolders.Remove(watchFolder);
                }
            }
        }

        /// <summary>
        /// Updates the <see cref="WatchFolder"/> object state if such object was found by the given <see cref="WatchFolderSettings"/> object.
        /// In case <c>WatchFolder</c> is found and <c>WatchFolder</c> is enabled by <c>WatchFolder.TaskSettings</c>
        /// <c>Enable</c> method will be called on found object, otherwise <c>WatchFolder</c> will be disposed.
        /// </summary>
        /// <param name="watchFolderSetting"><see cref="WatchFolderSettings"/> object.</param>
        public void UpdateWatchFolderState(WatchFolderSettings watchFolderSetting)
        {
            WatchFolder watchFolder = FindWatchFolder(watchFolderSetting);
            if (watchFolder != null)
            {
                if (watchFolder.TaskSettings.WatchFolderEnabled)
                {
                    watchFolder.Enable();
                }
                else
                {
                    watchFolder.Dispose();
                }
            }
        }

        /// <summary>
        /// Dispose all <see cref="WatchFolder"/> objects in the <see cref="WatchFolders"/> collection.
        /// </summary>
        public void UnregisterAllWatchFolders()
        {
            if (WatchFolders != null)
            {
                foreach (WatchFolder watchFolder in WatchFolders)
                {
                    if (watchFolder != null)
                    {
                        watchFolder.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Cleans all used resources.
        /// </summary>
        public void Dispose()
        {
            UnregisterAllWatchFolders();
        }
    }
}