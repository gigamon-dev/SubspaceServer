using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    public class Server
    {
        private ModuleManager _mm;

        public Server(string homeDirectory)
        {
            _mm = new ModuleManager();
        }

        public void Start()
        {
            loadModuleFile(null);

            //Network net = _mm.GetModule<Network>();
            //net.Start();
        }

        public void Stop()
        {
        }

        private void loadModuleFile(string filename)
        {
            // technically, if i just gave a directory to look for dlls in
            // i could use reflection to create the module objects
            // for now i'm just going to hardcode it

            _mm.AddModule(new ConfigManager());
            _mm.AddModule(new Mainloop());
            _mm.AddModule(new PlayerData());
            _mm.AddModule(new ArenaManager());

            _mm.LoadModules();
        }
    }
}
