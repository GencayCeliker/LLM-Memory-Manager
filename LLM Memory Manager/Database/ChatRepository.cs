using LLM_Memory_Manager.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LLM_Memory_Manager.Database
{
    public class ChatRepository
    {
        public async Task<User> GetOrCreateUserAsync(string phoneNumber, string userName)
        {
            using var context = new AppDbContext();

            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone_Number == phoneNumber);

            if (user == null)
            {
                user = new User
                {
                    Phone_Number = phoneNumber,
                    User_Name = userName,
                    Created_At = DateTime.Now
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();
            }

            return user;
        }

        public async Task<User?> GetUserByPhoneAsync(string phoneNumber)
        {
            using var context = new AppDbContext();
            return await context.Users.FirstOrDefaultAsync(u => u.Phone_Number == phoneNumber);
        }

        public async Task<User?> GetUserByIdAsync(int userId)
        {
            using var context = new AppDbContext();
            return await context.Users.FirstOrDefaultAsync(u => u.User_ID == userId);
        }

        public async Task<Session> CreateSessionAsync(int userId)
        {
            using var context = new AppDbContext();

            var session = new Session
            {
                User_ID = userId,
                Session_Start = DateTime.Now
            };

            context.Sessions.Add(session);
            await context.SaveChangesAsync();

            return session;
        }

        public async Task<Session?> GetLatestSessionAsync(int userId)
        {
            using var context = new AppDbContext();

            return await context.Sessions
                .Where(s => s.User_ID == userId)
                .OrderByDescending(s => s.Session_Start)
                .FirstOrDefaultAsync();
        }

        public async Task<Session> GetOrCreateLatestSessionAsync(int userId)
        {
            var latestSession = await GetLatestSessionAsync(userId);
            return latestSession ?? await CreateSessionAsync(userId);
        }

        public async Task<List<Session>> GetSessionsAsync(int userId)
        {
            using var context = new AppDbContext();

            return await context.Sessions
                .Where(s => s.User_ID == userId)
                .OrderByDescending(s => s.Session_Start)
                .ToListAsync();
        }

        public async Task<List<ChatMessage>> GetMessagesBySessionAsync(int sessionId)
        {
            using var context = new AppDbContext();

            return await context.ChatMessages
                .Where(m => m.Session_ID == sessionId)
                .OrderBy(m => m.Created_At)
                .ToListAsync();
        }

        public async Task<List<ChatMessage>> GetRecentMessagesAsync(int sessionId, int messageLimit)
        {
            using var context = new AppDbContext();

            var recentMessages = await context.ChatMessages
                .Where(m => m.Session_ID == sessionId)
                .OrderByDescending(m => m.Created_At)
                .Take(messageLimit)
                .ToListAsync();

            recentMessages.Reverse();
            return recentMessages;
        }

        public async Task SaveMessageAsync(int sessionId, int userId, string role, string content, int tokenCount = 0)
        {
            using var context = new AppDbContext();

            var message = new ChatMessage
            {
                Session_ID = sessionId,
                User_ID = userId,
                Role = role,
                Message_Content = content,
                Token_Count = tokenCount,
                Created_At = DateTime.Now
            };

            context.ChatMessages.Add(message);
            await context.SaveChangesAsync();
        }

        public async Task<AppSettings> GetOrCreateSettingsAsync()
        {
            using var context = new AppDbContext();

            var settings = await context.Settings.FirstOrDefaultAsync();

            if (settings == null)
            {
                settings = new AppSettings
                {
                    Max_Memory_Messages = 10,
                    Context_Length = 8192,
                    Cpu_Threads = 8,
                    Gpu_Layers = 38,
                    Temperature = 0.7,
                    Model_Name = "google/gemma-4-e4b"
                };

                context.Settings.Add(settings);
                await context.SaveChangesAsync();
            }

            return settings;
        }

        public async Task SaveSettingsAsync(AppSettings settings)
        {
            using var context = new AppDbContext();

            var existing = await context.Settings.FirstOrDefaultAsync();

            if (existing == null)
            {
                context.Settings.Add(new AppSettings
                {
                    Max_Memory_Messages = settings.Max_Memory_Messages,
                    Context_Length = settings.Context_Length,
                    Cpu_Threads = settings.Cpu_Threads,
                    Gpu_Layers = settings.Gpu_Layers,
                    Temperature = settings.Temperature,
                    Model_Name = settings.Model_Name
                });
            }
            else
            {
                existing.Max_Memory_Messages = settings.Max_Memory_Messages;
                existing.Context_Length = settings.Context_Length;
                existing.Cpu_Threads = settings.Cpu_Threads;
                existing.Gpu_Layers = settings.Gpu_Layers;
                existing.Temperature = settings.Temperature;
                existing.Model_Name = settings.Model_Name;
            }

            await context.SaveChangesAsync();
        }
    }
}

