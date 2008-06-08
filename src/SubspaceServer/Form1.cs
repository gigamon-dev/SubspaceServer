using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

using SS.Core;

namespace SS
{
    public partial class Form1 : Form
    {
        private Server server = new Server(Environment.CurrentDirectory);

        private bool started = false;

        public Form1()
        {
            InitializeComponent();

        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            startButton.PerformClick();
        }

        private void startButton_Click(object sender, EventArgs e)
        {
            if (started)
                return;

            server.Start();
            started = true;
            startButton.Enabled = false;
            stopBbutton.Enabled = true;
        }

        private void stopBbutton_Click(object sender, EventArgs e)
        {
            if (!started)
                return;

            server.Stop();
            started = false;
            startButton.Enabled = true;
            stopBbutton.Enabled = false;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if(started)
                server.Stop();

            base.OnClosing(e);
        }
    }
}