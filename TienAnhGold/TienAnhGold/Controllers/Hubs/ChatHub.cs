﻿using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TienAnhGold.Data;
using TienAnhGold.Models;

namespace TienAnhGold.Hubs
{
    public class ChatHub : Hub
    {
        private readonly TienAnhGoldContext _context;

        public ChatHub(TienAnhGoldContext context) => _context = context;

        public async Task SendMessage(string chatId, string senderId, string senderRole, string message)
        {
            try
            {
                if (!Context.User.Identity.IsAuthenticated) return;

                if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(senderRole) || string.IsNullOrEmpty(message))
                {
                    await Clients.Caller.SendAsync("ReceiveSystemMessage", "Hệ thống", "System", "Dữ liệu không hợp lệ: chatId, senderId, senderRole hoặc message không được để trống.");
                    return;
                }

                Console.WriteLine($"Sending message: chatId={chatId}, senderId={senderId}, senderRole={senderRole}, message={message}");

                var chat = await _context.Chats.FindAsync(int.Parse(chatId));
                if (chat == null || !chat.IsActive)
                {
                    await Clients.Caller.SendAsync("ReceiveSystemMessage", "Hệ thống", "System", "Đoạn chat không tồn tại hoặc đã kết thúc.");
                    return;
                }

                // Kiểm tra xem đây có phải là tin nhắn đầu tiên từ User không
                bool isFirstMessage = senderRole == "User" && !await _context.ChatMessages.AnyAsync(m => m.ChatId == int.Parse(chatId));

                // Lưu tin nhắn của người gửi (User)
                var chatMessage = new ChatMessage
                {
                    ChatId = int.Parse(chatId),
                    SenderId = senderId,
                    SenderRole = senderRole,
                    Message = message,
                    SentAt = DateTime.Now,
                    IsRead = false
                };
                _context.ChatMessages.Add(chatMessage);
                await _context.SaveChangesAsync();

                await NotifyUnreadCount(chatId, senderId, senderRole);

                // Gửi tin nhắn của User tới nhóm chat
                await Clients.Group(chatId).SendAsync("ReceiveMessage", senderId, senderRole, message);

                // Nếu là tin nhắn đầu tiên từ User, lưu và gửi tin nhắn hệ thống
                if (isFirstMessage)
                {
                    string systemMessage = "Cảm ơn bạn đã gửi tin nhắn. Bộ phận hỗ trợ của chúng tôi sẽ đến giúp bạn trong ít phút. Bạn cần hỗ trợ về điều gì? Hãy nói rõ cụ thể để bộ phận hỗ trợ đến và làm việc thật nhanh chóng, hiệu quả giúp bạn.";
                    var systemChatMessage = new ChatMessage
                    {
                        ChatId = int.Parse(chatId),
                        SenderId = "Hệ thống",
                        SenderRole = "System",
                        Message = systemMessage,
                        SentAt = DateTime.Now,
                        IsRead = false
                    };
                    _context.ChatMessages.Add(systemChatMessage);
                    await _context.SaveChangesAsync();

                    // Gửi tin nhắn hệ thống tới nhóm chat
                    await Clients.Group(chatId).SendAsync("ReceiveSystemMessage", "Hệ thống", "System", systemMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SendMessage: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                await Clients.Caller.SendAsync("ReceiveSystemMessage", "Hệ thống", "System", "Không thể gửi tin nhắn: " + ex.Message + (ex.InnerException != null ? " Inner: " + ex.InnerException.Message : ""));
            }
        }

        public async Task JoinChat(string chatId, string userId, string userRole)
        {
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, chatId);

                var chat = await _context.Chats.FindAsync(int.Parse(chatId));
                if (chat == null)
                {
                    await Clients.Caller.SendAsync("ReceiveSystemMessage", "Hệ thống", "System", "Đoạn chat không tồn tại.");
                    return;
                }

                Console.WriteLine($"JoinChat: chatId={chatId}, userId={userId}, userRole={userRole}, IsActive={chat.IsActive}, HasAdminJoinedMessage={chat.HasAdminJoinedMessage}, UserId={chat.UserId}, EmployeeId={chat.EmployeeId}");

                if ((userRole == "Employee" || userRole == "Admin") && chat.UserId != null && !chat.HasAdminJoinedMessage)
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == chat.UserId);
                    if (user != null)
                    {
                        Console.WriteLine($"Sending 'Admin... đã hỗ trợ bạn' message to group {chatId}");
                        await Clients.Group(chatId).SendAsync("ReceiveSystemMessage",
                            "Hệ thống",
                            "System",
                            userRole == "Admin" ? "Admin đã hỗ trợ bạn" : "Nhân viên đã hỗ trợ bạn");

                        chat.HasAdminJoinedMessage = true;
                        await _context.SaveChangesAsync();
                        Console.WriteLine($"Updated HasAdminJoinedMessage to true for chatId={chatId}");
                    }
                }

                if (chat.UserId != null && chat.EmployeeId != null)
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == chat.UserId);
                    if (user != null)
                    {
                        await Clients.Group(chatId).SendAsync("ReceiveSystemMessage",
                            "Hệ thống",
                            "System",
                            $"Chúng tôi đã vào và sẽ hỗ trợ cho bạn");
                    }
                }

                var messages = await _context.ChatMessages
                    .Where(m => m.ChatId == int.Parse(chatId) && !m.IsRead && m.SenderId != userId)
                    .ToListAsync();
                messages.ForEach(m => m.IsRead = true);
                await _context.SaveChangesAsync();

                await NotifyUnreadCount(chatId, userId, userRole);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in JoinChat: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                await Clients.Caller.SendAsync("ReceiveSystemMessage", "Hệ thống", "System", "Không thể tham gia chat: " + ex.Message + (ex.InnerException != null ? " Inner: " + ex.InnerException.Message : ""));
            }
        }

        public async Task LeaveChat(string chatId, string userId, string userRole)
        {
            try
            {
                var chat = await _context.Chats.FindAsync(int.Parse(chatId));
                if (chat == null) return;

                Console.WriteLine($"LeaveChat: chatId={chatId}, userId={userId}, userRole={userRole}, UserId={chat.UserId}, EmployeeId={chat.EmployeeId}");

                if (userRole == "User")
                {
                    chat.UserEnded = true;
                    Console.WriteLine($"User {userId} ended the chat: chatId={chatId}");
                }
                else if (userRole == "Employee")
                {
                    chat.EmployeeEnded = true;
                    Console.WriteLine($"Employee {userId} ended the chat: chatId={chatId}");
                }
                else if (userRole == "Admin")
                {
                    if (chat.UserId == userId)
                    {
                        chat.UserEnded = true;
                        Console.WriteLine($"Admin {userId} ended the chat as User: chatId={chatId}");
                    }
                    else if (chat.EmployeeId == userId)
                    {
                        chat.EmployeeEnded = true;
                        Console.WriteLine($"Admin {userId} ended the chat as Employee: chatId={chatId}");
                    }
                }

                await _context.SaveChangesAsync();

                Console.WriteLine($"Sending 'Đoạn chat đã kết thúc' to group: {chatId}");
                await Clients.Group(chatId).SendAsync("ReceiveSystemMessage",
                    "Hệ thống",
                    "System",
                    "Đoạn chat đã kết thúc.");

                await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatId);
                Console.WriteLine($"Removed connection from group: chatId={chatId}, userId={userId}, userRole={userRole}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in LeaveChat: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
        }

        public async Task NotifyChatFullyEnded(string chatId)
        {
            try
            {
                Console.WriteLine($"Notifying group {chatId} that chat has fully ended.");
                await Clients.Group(chatId).SendAsync("ChatFullyEnded");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in NotifyChatFullyEnded: {ex.Message}");
            }
        }

        private async Task NotifyUnreadCount(string chatId, string senderId, string senderRole)
        {
            try
            {
                var chat = await _context.Chats.FindAsync(int.Parse(chatId));
                if (chat == null) return;

                if ((senderRole == "Employee" || senderRole == "Admin") && chat.UserId != null)
                {
                    var unreadCount = await _context.ChatMessages
                        .Where(m => m.ChatId == int.Parse(chatId) && (m.SenderRole == "Employee" || m.SenderRole == "Admin") && !m.IsRead)
                        .CountAsync();
                    await Clients.User(chat.UserId).SendAsync("UpdateUnreadCount", unreadCount);
                }
                else if (senderRole == "User" && chat.EmployeeId != null)
                {
                    var unreadUserCount = await _context.ChatMessages
                        .Where(m => m.SenderRole == "User" && !m.IsRead)
                        .GroupBy(m => m.ChatId)
                        .Select(g => new { ChatId = g.Key, Count = g.Count() })
                        .ToListAsync();
                    var totalUnread = unreadUserCount.Sum(x => x.Count);
                    await Clients.User(chat.EmployeeId).SendAsync("UpdateEmployeeUnreadCount", totalUnread, unreadUserCount);
                }
                else if (senderRole == "Employee" && chat.EmployeeId != null && chat.UserId != null)
                {
                    var unreadEmployeeCount = await _context.ChatMessages
                        .Where(m => m.SenderRole == "Employee" && !m.IsRead)
                        .GroupBy(m => m.ChatId)
                        .Select(g => new { ChatId = g.Key, Count = g.Count() })
                        .ToListAsync();
                    var totalUnread = unreadEmployeeCount.Sum(x => x.Count);
                    await Clients.User(chat.UserId).SendAsync("UpdateAdminUnreadCount", totalUnread, unreadEmployeeCount);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in NotifyUnreadCount: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
        }
    }
}