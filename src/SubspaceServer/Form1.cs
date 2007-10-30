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

        public Form1()
        {
            InitializeComponent();
        }

        private void startButton_Click(object sender, EventArgs e)
        {
            server.Start();
        }

        private void stopBbutton_Click(object sender, EventArgs e)
        {
            server.Stop();
        }
    }
}