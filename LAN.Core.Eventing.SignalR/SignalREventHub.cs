﻿using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LAN.Core.DependencyInjection;
using LAN.Core.Eventing.Server;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using Newtonsoft.Json.Linq;

namespace LAN.Core.Eventing.SignalR
{
	[HubName("eventHub")]
	public class SignalREventHub : Hub
	{
		private readonly IMessagingContext _messagingContext;
		private readonly IHandlerRepository _handlerRepository;
		private readonly ISignalRGroupRegistrar _groupRegistrar;
		private readonly IGroupJoinService _groupJoinService;
		private readonly IGroupLeaveService _groupLeaveService;

		public SignalREventHub()
		{
			try
			{
				if (ContainerRegistry.DefaultContainer == null) throw new Exception("You have not set the default container.  ContainerRegistry.DefaultContainer");
				var container = ContainerRegistry.DefaultContainer;
				this._handlerRepository = container.GetInstance<IHandlerRepository>();
				this._messagingContext = container.GetInstance<IMessagingContext>();
				this._groupRegistrar = container.GetInstance<ISignalRGroupRegistrar>();
				this._groupJoinService = container.GetInstance<IGroupJoinService>();
				this._groupLeaveService = container.GetInstance<IGroupLeaveService>();
			}
			catch (Exception ex)
			{
				OnExceptionOccurred(new SignalRExceptionEventArgs(this.Context.User, ex, new SignalRConnectionContext(this.Context)));
				throw;
			}
		}

		[HubMethodName("raiseEvent")]
		// ReSharper disable once UnusedMember.Global
		public void RaiseEvent(string eventName, JObject data)
		{
			try
			{
				IHandler handler;
				if (!this._handlerRepository.TryGetHandler(eventName, out handler))
				{
					var errorMessage = string.Format(
						"The event {0} is not a known event, there are only a few things this could be, check that you have Registered the Handler with your HandlerRepository, that its registered with the event you are emitting {0}, and that all of the handler's dependancies are registered with your DI container.",
						eventName);
					SendErrorToClient(errorMessage);
					throw new ArgumentException(errorMessage, "eventName");
				}

				var deserializedRequest = (RequestBase)data.ToObject(handler.GetRequestType());
				deserializedRequest.ConnectionContext = new SignalRConnectionContext(this.Context);

				if (!handler.IsAuthorized(deserializedRequest, this.Context.User))
				{
					var errorMessage = string.Format(
						"Auth: You are not authorized to use the event {0}.",
						eventName);
					SendErrorToClient(errorMessage);
					throw new AuthenticationException(errorMessage);
				}

				var eventTask = new Task(() => handler.Invoke(deserializedRequest, this.Context.User));
				eventTask.ContinueWith(HandleErrorForAsyncHandler, TaskContinuationOptions.OnlyOnFaulted);
				eventTask.Start();

				Debug.WriteLine("SignalR EventHub: {0} has raised {1} event", this.Context.User.Identity.Name, eventName);
			}
			catch (Exception ex)
			{
				OnExceptionOccurred(new SignalRExceptionEventArgs(this.Context.User, ex, new SignalRConnectionContext(this.Context)));
				SendExceptionToClient("An unknown error has occurred", ex);
			}
		}

		#region Connect

		public override Task OnConnected()
		{
			var username = this.Context.User.Identity.Name;
			if (!this.Context.User.Identity.IsAuthenticated) return base.OnConnected();
			try
			{
				var groupsToJoin = this._groupRegistrar.GetGroupsForUser(username);
				foreach (var groupToJoin in groupsToJoin.Result)
				{
					this._groupJoinService.JoinToGroup(groupToJoin, this.Context.ConnectionId);
					Debug.WriteLine("SignalR EventHub: {0} has joined group {1}", username, groupToJoin);
				}
				OnUserConnected(new SignalRUserConnectedEventArgs(this.Context.User, new SignalRConnectionContext(this.Context)));
			}
			catch (Exception ex)
			{
				OnExceptionOccurred(new SignalRExceptionEventArgs(this.Context.User, ex, new SignalRConnectionContext(this.Context)));
				SendExceptionToClient("Error Joining Groups", ex);
			}

			return base.OnConnected();
		}

		/// <summary>
		/// Will be raised when a client's browser connects
		/// </summary>
		public static event EventHandler<SignalRUserConnectedEventArgs> UserConnected;

		private static void OnUserConnected(SignalRUserConnectedEventArgs e)
		{
			EventHandler<SignalRUserConnectedEventArgs> handler = UserConnected;
			if (handler != null) handler(null, e);
		}

		#endregion

		#region Disconnect

		public override Task OnDisconnected(bool stopCalled)
		{
			var username = this.Context.User.Identity.Name;
			if (!this.Context.User.Identity.IsAuthenticated) return base.OnDisconnected(stopCalled);
			try
			{
				var groupsToLeave = this._groupRegistrar.GetGroupsForUser(username);
				foreach (var groupToJoin in groupsToLeave.Result)
				{
					this._groupLeaveService.LeaveGroup(groupToJoin, this.Context.ConnectionId);
					Debug.WriteLine("SignalR EventHub: {0} has left group {1}", username, groupToJoin);
				}
				OnUserDisconnected(new SignalRUserDisconnectedEventArgs(this.Context.User, new SignalRConnectionContext(this.Context)));
			}
			catch (Exception ex)
			{
				OnExceptionOccurred(new SignalRExceptionEventArgs(this.Context.User, ex, new SignalRConnectionContext(this.Context)));
				SendExceptionToClient("Error Leaving Groups", ex);
			}

			return base.OnDisconnected(stopCalled);
		}

		/// <summary>
		/// Will be raised when a client's browser disconnects
		/// </summary>
		public static event EventHandler<SignalRUserDisconnectedEventArgs> UserDisconnected;

		private static void OnUserDisconnected(SignalRUserDisconnectedEventArgs e)
		{
			EventHandler<SignalRUserDisconnectedEventArgs> handler = UserDisconnected;
			if (handler != null) handler(null, e);
		}

		#endregion

		#region Errors

		/// <summary>
		/// When sending errors to the client, this will be used to decide if the full exception details should be sent down to the client.
		/// </summary>
		public static bool SendFullStackTraceToClient = false;

		/// <summary>
		/// Will be raised when an exception occurs within the event hub.
		/// </summary>
		public static event EventHandler<SignalRExceptionEventArgs> ExceptionOccurred;

		private static void OnExceptionOccurred(SignalRExceptionEventArgs e)
		{
			EventHandler<SignalRExceptionEventArgs> handler = ExceptionOccurred;
			if (handler != null) handler(null, e);
		}

		private void HandleErrorForAsyncHandler(Task handlerTask)
		{
			if (handlerTask.Exception == null) return; //this should never happen, since this will only be used for faulted tasks

			var exception = handlerTask.Exception.GetBaseException();
			OnExceptionOccurred(new SignalRExceptionEventArgs(this.Context.User, exception, new SignalRConnectionContext(this.Context)));
			SendExceptionToClient("Handler Exception", exception);
		}

		private void SendExceptionToClient(string baseMessage, Exception ex)
		{
			var message = "";
			if (SendFullStackTraceToClient)
			{
				message = Environment.NewLine + ex.ToString();
			}
			SendErrorToClient(baseMessage + message);
		}

		private void SendErrorToClient(string message)
		{
			Debug.WriteLine("SignalR EventHub: Error: \n{0}", message);
			this._messagingContext.PublishToClient(new EventName(ServerEvents.OnError), new OnErrorResponse(null) { CorrelationId = this.Context.ConnectionId, Message = message });
		}

		#endregion
	}
}
