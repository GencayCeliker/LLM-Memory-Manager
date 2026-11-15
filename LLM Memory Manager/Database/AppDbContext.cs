using LLM_Memory_Manager.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;
namespace LLM_Memory_Manager.Database
{
    public class AppDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }
        public DbSet<Session> Sessions { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<AppSettings> Settings { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Veri tabanı bağlantı cümlemiz (Connection String)
            // "YOUR_SERVER_NAME" kısmını kendi SQL Server adınla (örneğin: .\SQLEXPRESS veya localhost) değiştirmeyi unutma.
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer(@"Server=.\SQLEXPRESS;Database=LLM;Trusted_Connection=True;TrustServerCertificate=True;");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // C# sınıflarımızın veri tabanındaki tam tablo ve Primary Key karşılıklarını belirtiyoruz
            modelBuilder.Entity<User>().ToTable("T_User").HasKey(u => u.User_ID);

            modelBuilder.Entity<Session>().ToTable("T_Session").HasKey(s => s.Session_ID);

            modelBuilder.Entity<ChatMessage>().ToTable("T_Chat").HasKey(c => c.Message_ID);

            modelBuilder.Entity<AppSettings>().ToTable("T_Settings");
            modelBuilder.Entity<AppSettings>().HasKey(a => a.Setting_ID);
            modelBuilder.Entity<AppSettings>().Property(a => a.Setting_ID).ValueGeneratedOnAdd();
        }
    }
}

