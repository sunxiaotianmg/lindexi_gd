﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace LaykearduchuNachairgurharhear
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
      
    }

    class Program
    {
        [STAThread]
        static void Main()
        {
            using (new HinembereneabemWhejurnicelem.App())
            {
                App app = new App();
                app.InitializeComponent();
                app.Run();
            }
        }
    }
}
