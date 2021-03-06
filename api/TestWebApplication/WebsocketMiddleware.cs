﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Data.DTO;
using Data.UnitOfWork;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace TestWebApplication
{
    public class WebSocketMiddleware
    {
        private static ConcurrentDictionary<int, ConcurrentDictionary<string, WebSocket>> _websockets;

        private readonly RequestDelegate _next;

        public WebSocketMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        #region dictionary access methods
        private ConcurrentDictionary<int, ConcurrentDictionary<string, WebSocket>> WebSockets =>
            _websockets ?? (_websockets = new ConcurrentDictionary<int, ConcurrentDictionary<string, WebSocket>>());

        public void AddWebsocketToThread(int threadId, string socketId, WebSocket socket)
        {
            var threadSockets = WebSockets.GetOrAdd(threadId, new ConcurrentDictionary<string, WebSocket>());
            threadSockets.TryAdd(socketId, socket);
            WebSockets.AddOrUpdate(threadId, threadSockets, (i, sockets) => threadSockets);
        }

        public void RemoveSocketFromThread(int threadId, string socketId)
        {
            if (WebSockets.TryGetValue(threadId, out var threadSockets))
            {
                threadSockets.TryRemove(socketId, out var dummy);
                if (threadSockets.Count < 1)
                {
                    WebSockets.TryRemove(threadId, out var dummy2);
                }
                else
                {
                    WebSockets.AddOrUpdate(threadId, threadSockets, (i, sockets) => threadSockets);
                }
            }
        }

        public ConcurrentDictionary<string, WebSocket> GetAllSocketsForThread(int threadId)
        {
            return WebSockets.GetOrAdd(threadId, new ConcurrentDictionary<string, WebSocket>());
        }

        public WebSocket GetSocketByGuid(int threadId, string id)
        {
            if (WebSockets.TryGetValue(threadId, out var threadSockets))
            {
                if (threadSockets.TryGetValue(id.ToString(), out var socket))
                {
                    return socket;
                }
            }

            return null;
        }

        #endregion

        public async Task Invoke(HttpContext context, IHostingEnvironment env, IUnitOfWork work)
        {
            if (!context.WebSockets.IsWebSocketRequest || !context.Request.Path.HasValue)
            {
                await _next.Invoke(context);
                return;
            }
            var split = context.Request.Path.Value.Split('/');

            if (split.Length < 5 || !int.TryParse(split[5], out var threadId))
            {
                await _next.Invoke(context);
                return;
            }

            CancellationToken ct = context.RequestAborted;
            WebSocket currentSocket = await context.WebSockets.AcceptWebSocketAsync();
            var socketId = Guid.NewGuid().ToString();

            AddWebsocketToThread(threadId, socketId, currentSocket);

            var sendJwt = JsonConvert.SerializeObject(new WebSocketMessage(){ Guid = TokenHandler.GenerateJwt(socketId) }, new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

            await SendStringAsync(currentSocket, sendJwt, ct);

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var response = await ReceiveStringAsync(currentSocket, ct);
                if (string.IsNullOrEmpty(response))
                {
                    if (currentSocket.State != WebSocketState.Open)
                    {
                        break;
                    }

                    continue;
                }

                var webSocketResponse = JsonConvert.DeserializeObject<WebSocketMessage>(response);
                try
                {
                    webSocketResponse.Post.ThreadId = threadId;
                    var board = PostHelper.CreatePost(work, env, context.Request, webSocketResponse.Post);

                    var data = JsonConvert.SerializeObject(board, new JsonSerializerSettings
                    {
                        ContractResolver = new CamelCasePropertyNamesContractResolver()
                    });

                    foreach (var socket in GetAllSocketsForThread(threadId))
                    {
                        if (socket.Value.State != WebSocketState.Open)
                        {
                            continue;
                        }

                        await SendStringAsync(socket.Value, data, ct);
                    }
                }
                catch (PostException e)
                {
                    if (!TokenHandler.JwtValid(webSocketResponse.Guid, out var guid)) continue;
                    var socket = GetSocketByGuid(threadId, guid);
                    if (socket == null) continue;
                    var error = JsonConvert.SerializeObject(new {message = e.Message},
                        new JsonSerializerSettings
                        {
                            ContractResolver = new CamelCasePropertyNamesContractResolver()
                        });

                    if (currentSocket.State != WebSocketState.Open)
                    {
                        continue;
                    }

                    await SendStringAsync(socket, error, ct);
                }
                catch (Exception)
                {
                    if (!TokenHandler.JwtValid(webSocketResponse.Guid, out var guid)) continue;
                    var socket = GetSocketByGuid(threadId, guid);
                    if (socket == null) continue;
                    var error = JsonConvert.SerializeObject(new {message = "An error occurred."},
                        new JsonSerializerSettings
                        {
                            ContractResolver = new CamelCasePropertyNamesContractResolver()
                        });

                    if (currentSocket.State != WebSocketState.Open)
                    {
                        continue;
                    }

                    await SendStringAsync(socket, error, ct);
                }
            }

            RemoveSocketFromThread(threadId, socketId);

            await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
            currentSocket.Dispose();
        }

        #region send/receive
        private static Task SendStringAsync(WebSocket socket, string data, CancellationToken ct = default(CancellationToken))
        {
            var buffer = Encoding.UTF8.GetBytes(data);
            var segment = new ArraySegment<byte>(buffer);
            return socket.SendAsync(segment, WebSocketMessageType.Text, true, ct);
        }

        private static async Task<string> ReceiveStringAsync(WebSocket socket, CancellationToken ct = default(CancellationToken))
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    ct.ThrowIfCancellationRequested();

                    result = await socket.ReceiveAsync(buffer, ct);
                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    return null;
                }

                // Encoding UTF8: https://tools.ietf.org/html/rfc6455#section-5.6
                using (var reader = new StreamReader(ms, Encoding.UTF8))
                {
                    return await reader.ReadToEndAsync();
                }
            }
        }
        #endregion
    }

    public class WebSocketMessage
    {
        public string Guid { get; set; }
        public PostDTO Post { get; set; }
    }
}
