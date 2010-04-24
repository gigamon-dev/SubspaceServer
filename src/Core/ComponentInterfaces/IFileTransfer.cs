using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public interface IFileTransfer : IComponentInterface
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="p">The player to send file to.</param>
        /// <param name="path">The path and file name.</param>
        /// <param name="filename">The name of the uploaded file.</param>
        /// <param name="deleteAfter">Whether the file should be deleted after it is sent.</param>
        /// <returns></returns>
        bool SendFile(Player p, string path, string filename, bool deleteAfter);

        // TODO: 
        //bool RequestFile<T>(Player p, string path, Action<string, T> uploaded);
        //void SetWorkingDirectory(Player p, string path);
        //string GetWorkingDirectory(Player p);
    }
}
