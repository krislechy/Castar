using System;
using System.Management;
using System.Windows;

namespace Castar
{
    public partial class App : Application
    {
        private string GetUniqueMachineId()
        {
            var serialNumber = GetSerialNumberMotherBoard();
            var processorId = GetProcessorId();
            return (serialNumber ?? "-") + "." + (processorId ?? "-");
        }
        private string? GetSerialNumberMotherBoard()
        {
            ManagementObjectSearcher mos = new ManagementObjectSearcher("SELECT * FROM Win32_BaseBoard");
            ManagementObjectCollection moc = mos.Get();
            string? serialNumber = null;
            foreach (ManagementObject mo in moc)
            {
                serialNumber = (string?)mo["SerialNumber"];
            }
            return serialNumber;
        }
        private string? GetProcessorId()
        {
            ManagementObjectCollection mbsList = null;
            ManagementObjectSearcher mbs = new ManagementObjectSearcher("Select * From Win32_processor");
            mbsList = mbs.Get();
            string? id = null;
            foreach (ManagementObject mo in mbsList)
            {
                id = (string?)mo["ProcessorID"];
            }
            return id;
        }
    }
}
