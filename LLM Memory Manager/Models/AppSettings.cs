using System;
using System.Collections.Generic;
using System.Text;

namespace LLM_Memory_Manager.Models
{
    public class AppSettings
    {
        public int Setting_ID { get; set; }
        public int Max_Memory_Messages { get; set; }
        public int Context_Length { get; set; }
        public int Cpu_Threads { get; set; }
        public int Gpu_Layers { get; set; }
        public double Temperature { get; set; }
        public string? Model_Name { get; set; }
    }
}
