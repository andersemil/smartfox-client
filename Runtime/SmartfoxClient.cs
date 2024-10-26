using UnityEngine;
using UnityEngine.Events;

using System;
using System.Collections.Generic;

using Sfs2X;
using Sfs2X.Logging;
using Sfs2X.Util;
using Sfs2X.Requests;
using Sfs2X.Core;
using Sfs2X.Entities;
using Sfs2X.Entities.Data;
using Sfs2X.Entities.Variables;
using User = Sfs2X.Entities.User;

namespace Smartfox {

	public class SmartfoxClient : MonoBehaviour {
		public bool Verbose;

		/// <summary>
		/// Invoked when connection is unstable or slow, but not disconnected
		/// </summary>
		public UnityEvent OnPoorConnection;
		/// <summary>
		/// Invoked when connection is not unstable or slow anymore
		/// </summary>
		public UnityEvent OnConnectionResumed;
		public UnityEvent OnDisconnected;
		public UnityEvent<User, Room> OnUserLeftRoom;
		public UnityEvent<ISFSObject, User> OnObjectMessage;
		public UnityEvent<string, User> OnPrivateMessage;

		public static ISFSObject ObjectMessageToHost = new SFSObject ();
		public static List<string> MessagesToHost = new ();
		public static SmartfoxClient Instance;

		/// <summary>
		/// The current lag from client to server
		/// </summary>
		public static int LagValue;

		/// <summary>
        /// True while we are experiencing a poor connection to the smartfox server
        /// </summary>
		public static bool PoorConnection;

		public static User HostUser {
			get {
				return HostUserList.Count != 0 ? HostUserList [0] : null;
			}
			set {
				HostUserList.Clear ();
				if (value != null) {
					HostUserList.Add (value);
				}
			}
		}
		private static readonly List<User> HostUserList = new (1);

		public static User MySfsUser { get { return sfs?.MySelf; } }

		private static SmartFox sfs;

		private float lastLagWarningTime;

		/// <summary>
		/// Threshold in miliseconds above which we display a toast message about network lag.
		/// </summary>
		public static int LagWarningThreshold = 250;

		/// <summary>
		/// Minimum time in seconds between each lag warning toast message.
		/// </summary>
		public static int LagWarningInterval = 60;

		/// <summary>
		/// How often Smartfox should measure lag value
		/// </summary>
		public static int LagMeasureIntervalSeconds = 3;

		/// <summary>
		/// How many samples Smartfox should use to calculate average lag value
		/// </summary>
		public static int LagMeasureNumSamples = 3;

		private float NextCheckReachableTime;

		private static MulticastDelegate OnResultDelegate;

		void Awake () {
#if UNITY_WEBPLAYER
			/*|| UNITY_WEBGL ???*/
			if (!Security.PrefetchSocketPolicy(SmartfoxHost, SmartfoxTcpPort, 500)) {
				Debug.LogError("Security Exception. Policy file loading failed!");
			}
#endif
			Instance = this;
		}

		void Update () {
			if (sfs != null) {
#if false
			if (Input.GetKeyDown (KeyCode.S)) {
				Debug.Log ("Smartfox spawn");
				/*var _name = "friend_" + UnityEngine.Random.Range (0, 100);
				var instance = new SFSObject ();
				instance.PutUtfString ("_name", _name);
				instance.PutUtfString ("name", "Bennybjørn");
				var dataObj = new SFSObject ();
				dataObj.PutUtfString ("spawn", "friend-list");		//route message only to the SpawnComponent with Key="friend-list"
				dataObj.PutUtfString ("friend-list", "friendy_1,friendy_53," + _name);		//spawn named instance of "friend-list"
				dataObj.PutSFSObject ("_instance", instance);		//initialize instance with properties from instance object
				ReplicatedComponent.InvokeObjectMessage (dataObj, 0);*/
				InvokeObjectMessage (SFSObject.NewFromJsonData (@"{
	""friend-list"": ""friend_annifrid,friend_agnetha,friend_benny,friend_bjoern"",
	""_instances"": [
		{""_name"":""friend_annifrid"", ""name"":""Annifrid""},
		{""_name"":""friend_agnetha"",  ""name"":""Agnetha""}
    ]
}"), 0);
				}
#endif
				var om2h = ObjectMessageToHost;
				if (om2h.Size () != 0) {
					if (Verbose) {
						Debug.Log ("[SmartfoxClient] Sending to host: " + om2h.GetDump ());
					}
					ObjectMessageToHost = new SFSObject ();
					try {
						Send (om2h);
					} catch (Exception) {
						Debug.LogError ("Exception while sending object message to host. Dump: " + om2h.GetDump ());
					}
				}
				if (MessagesToHost.Count != 0) {
					try {
						foreach (var msg in MessagesToHost) {
							if (Verbose) {
								Debug.Log ("[SmartfoxClient] Sending private message to host: " + msg);
							}
							Send (msg);
						}
					} catch (Exception) {
						Debug.LogError ("Exception while sending private messages to host. Messages:\n" + string.Join ('\n', MessagesToHost));
					}
					MessagesToHost.Clear ();
				}

				sfs.ProcessEvents ();

				//Since smartfox' CONNECTION_RETRY event is unreliable (at least on iPhone), we detect manually when internet is unreachable
				var t = Time.realtimeSinceStartup;
				if (t >= NextCheckReachableTime) {
					NextCheckReachableTime = t + 1f;
					if (Application.internetReachability == NetworkReachability.NotReachable) {
						if (!PoorConnection) {
							PoorConnection = true;
							OnPoorConnection?.Invoke ();
						}
					} else if (PoorConnection) {
						PoorConnection = false;
						OnConnectionResumed?.Invoke ();
					}
				}
			}
		}

		public static void Connect (string host, string zone, Action<bool> callback, int port = 9933) {
			OnResultDelegate = callback;

			//If previously connected, reset state and clean up
			Instance.Reset ();

			// Set connection parameters
			var cfg = new ConfigData {
				Host = host,
#if !UNITY_WEBGL
				Port = port,
#else
			Port = 8833,
#endif
				Zone = zone
			};

			// Initialize SFS2X client and add listeners
#if !UNITY_WEBGL
			sfs = new SmartFox (true);
#else
		sfs = new SmartFox (UseWebSocket.WS);
#endif

			// Set ThreadSafeMode explicitly, or Windows Store builds will get a wrong default value (false)
			sfs.ThreadSafeMode = true;

			Instance.SubscribeEvents ();

			var msg = $"Connecting to server at {cfg.Host}:{cfg.Port} zone={cfg.Zone}";
			if (Instance.Verbose) {
				Debug.Log (msg);
			}

			// Connect to SFS2X
			sfs.Connect (cfg);
		}

		void SubscribeEvents () {
			sfs.AddEventListener (SFSEvent.CONNECTION, OnConnection);
			sfs.AddEventListener (SFSEvent.CONNECTION_LOST, OnConnectionLost);
			sfs.AddEventListener (SFSEvent.CONNECTION_RETRY, OnConnectionRetry);
			sfs.AddEventListener (SFSEvent.CONNECTION_RESUME, OnConnectionResume);
			sfs.AddEventListener (SFSEvent.LOGIN, OnLogin);
			sfs.AddEventListener (SFSEvent.LOGIN_ERROR, OnLoginError);
			sfs.AddEventListener (SFSEvent.ROOM_JOIN, OnRoomJoin);
			sfs.AddEventListener (SFSEvent.ROOM_JOIN_ERROR, OnRoomJoinError);
			sfs.AddEventListener (SFSEvent.ROOM_CREATION_ERROR, OnRoomCreationError);
			sfs.AddEventListener (SFSEvent.OBJECT_MESSAGE, _OnObjectMessage);
			sfs.AddEventListener (SFSEvent.PRIVATE_MESSAGE, _OnPrivateMessage);
			//sfs.AddEventListener(SFSEvent.USER_VARIABLES_UPDATE, OnUserVariableUpdate);
			sfs.AddEventListener (SFSEvent.USER_ENTER_ROOM, OnUserEnterRoom);
			sfs.AddEventListener (SFSEvent.USER_EXIT_ROOM, OnUserExitRoom);
			sfs.AddEventListener (SFSEvent.PING_PONG, OnPingPong);

			sfs.Logger.EnableEventDispatching = true;
			sfs.Logger.LoggingLevel = LogLevel.WARN;
			sfs.AddLogListener (LogLevel.WARN, OnWarnLogMessage);
		}

		void OnWarnLogMessage (BaseEvent evt) {
			string message = (string)evt.Params ["message"];
			Debug.LogWarningFormat ("[SFS2X] WARN {0}", message);
		}

		private void Reset () {
			if (sfs != null) {
				try {
					// Remove SFS2X listeners
					sfs.RemoveEventListener (SFSEvent.CONNECTION, OnConnection);
					sfs.RemoveEventListener (SFSEvent.CONNECTION_LOST, OnConnectionLost);
					sfs.RemoveEventListener (SFSEvent.CONNECTION_RETRY, OnConnectionRetry);
					sfs.RemoveEventListener (SFSEvent.CONNECTION_RESUME, OnConnectionResume);
					sfs.RemoveEventListener (SFSEvent.LOGIN, OnLogin);
					sfs.RemoveEventListener (SFSEvent.LOGIN_ERROR, OnLoginError);
					sfs.RemoveEventListener (SFSEvent.ROOM_JOIN, OnRoomJoin);
					sfs.RemoveEventListener (SFSEvent.ROOM_JOIN_ERROR, OnRoomJoinError);
					sfs.RemoveEventListener (SFSEvent.ROOM_CREATION_ERROR, OnRoomCreationError);
					sfs.RemoveEventListener (SFSEvent.OBJECT_MESSAGE, _OnObjectMessage);
					sfs.RemoveEventListener (SFSEvent.PRIVATE_MESSAGE, _OnPrivateMessage);
					//sfs.RemoveEventListener(SFSEvent.USER_VARIABLES_UPDATE, OnUserVariableUpdate);
					sfs.RemoveEventListener (SFSEvent.USER_ENTER_ROOM, OnUserEnterRoom);
					sfs.RemoveEventListener (SFSEvent.USER_EXIT_ROOM, OnUserExitRoom);
					sfs.RemoveEventListener (SFSEvent.PING_PONG, OnPingPong);
					if (sfs.IsConnected || sfs.IsConnecting) {
						Debug.Log ("Smartfox disconnecting");
						sfs.Disconnect ();
					}
					sfs = null;
				} catch (Exception) {
					//Dont care
				}
			}
			HostUser = null;
		}

		public static void JoinRoom (string roomName, bool asSpectator, Action<Room, bool> callback) {
			if (sfs == null || !sfs.IsConnected) {
				Debug.LogError ("SmartfoxClient not connected");
				return;
			}
			OnResultDelegate = callback;
			sfs.Send (new JoinRoomRequest (roomName, "", null, asSpectator));
		}

		public static void CreateAndJoinRoom (string roomName, int maxPlayers, int maxSpectators, List<RoomVariable> roomVariables, Action<Room, bool> callback) {
			if (sfs == null || !sfs.IsConnected) {
				Debug.LogError ("SmartfoxClient not connected");
				return;
			}
			var roomSettings = new RoomSettings (roomName) {
				IsGame = true,
				MaxUsers = (short)maxPlayers,
				MaxSpectators = (short)maxSpectators
			};
			roomSettings.Variables = roomVariables;
			if (Debug.isDebugBuild) {
				roomSettings.Variables.Add (new SFSRoomVariable ("IsTest", true));
			}
			roomSettings.Permissions = new RoomPermissions {
				AllowResizing = true,
				AllowNameChange = false,
				AllowPasswordStateChange = true,
				AllowPublicMessages = true
			};
			OnResultDelegate = callback;
			sfs.Send (new CreateRoomRequest (roomSettings, true));
		}

		public static void LeaveRoom (Room room) {
			sfs.Send (new LeaveRoomRequest (room));
		}

		public static User GetUserByName (string name) {
			return sfs.UserManager.GetUserByName (name);
		}

		private void OnConnection (BaseEvent evt) {
			var success = (bool)evt.Params ["success"];
			if (!success) {
				if (Verbose) {
					Debug.LogError ("Failed to connect to smartfoxserver");
				}
				// Remove SFS2X listeners and re-enable interface
				Reset ();
			}
			((Action<bool>)OnResultDelegate)?.Invoke (success);
		}

		public static void Login (string name, Action<string> callback) {
			if (sfs == null || !sfs.IsConnected) {
				Debug.LogError ("SmartfoxClient not connected");
				return;
			}
			OnResultDelegate = callback;
			sfs.Send (new LoginRequest (name ?? string.Empty));
		}

		private void OnConnectionRetry (BaseEvent evt) {
			if (Verbose) {
				Debug.LogWarning ("SFS Connection retry ...");
			}
			OnPoorConnection?.Invoke ();
		}

		private void OnConnectionResume (BaseEvent evt) {
			if (Verbose) {
				Debug.Log ("SFS Connection resumed!");
			}
			OnConnectionResumed?.Invoke ();
		}

		private void OnPingPong (BaseEvent evt) {
			if (HostUser == null) {
				return;
			}
			LagValue = (int)evt.Params ["lagValue"];
			if (Verbose) {
				Debug.LogFormat ("[SmartfoxClient] LagMonitor: {0} ms", LagValue);
			}

			if (LagValue >= LagWarningThreshold) {
				if ((int)(Time.realtimeSinceStartup - lastLagWarningTime) >= LagWarningInterval) {
					lastLagWarningTime = Time.realtimeSinceStartup;
					if (!PoorConnection) {
						PoorConnection = true;
						OnPoorConnection?.Invoke ();
					}
				}
			} else if (PoorConnection) {
				PoorConnection = false;
				OnConnectionResumed?.Invoke ();
			}
			//Analytics.LogEvent ("Lag", Analytics.ParameterLevelName, GameType, Analytics.ParameterValue, lagValue);
		}

		private void OnConnectionLost (BaseEvent evt) {
			Debug.Log ("Smartfox connection lost");

			// Remove SFS2X listeners and re-enable interface
			Reset ();

			var reason = (string)evt.Params ["reason"];
			if (reason != ClientDisconnectionReason.MANUAL) {
				Debug.LogWarning ("SFS Connection was lost; reason is: " + reason);
			} else if (Verbose) {
				Debug.Log ("Client was disconnected.");
			}
			OnDisconnected?.Invoke ();
		}

		private void OnLogin (BaseEvent evt) {
			if (Verbose) {
				// Show system message
				string msg = "SFS Connection established successfully\n";
				msg += "SFS2X API version: " + sfs.Version + "\n";
				msg += "Connection mode is: " + sfs.ConnectionMode + "\n";
				msg += "Logged in as " + MySfsUser.Name + ", Id=" + MySfsUser.Id;
				Debug.Log (msg);
			}

			if (Verbose) {
				Debug.LogFormat ("Lag monitoring enabled with warning threshold of {0} ms", LagWarningThreshold);
			}
			sfs.EnableLagMonitor (true, LagMeasureIntervalSeconds, LagMeasureNumSamples);

			((Action<string>)OnResultDelegate)?.Invoke (null);
		}

		private void OnLoginError (BaseEvent evt) {
			// Disconnect
			sfs.Disconnect ();

			// Remove SFS2X listeners and re-enable interface
			Reset ();

			var error = (string)evt.Params ["errorMessage"];
			((Action<string>)OnResultDelegate)?.Invoke (error);
		}

		private void OnRoomJoin (BaseEvent evt) {
			var room = (Room)evt.Params ["room"];

			//Find game admin, if already joined
			if (room.ContainsVariable ("HostId")) {
				int hostId = room.GetVariable ("HostId").GetIntValue ();
				foreach (User user in room.UserList) {
					if (user.Id == hostId) {
						if (Verbose) {
							Debug.Log ("Found GameHost Id=" + user.Id);
						}
						HostUser = user;
						break;
					}
				}
			}

			((Action<Room, bool>)OnResultDelegate)?.Invoke (room, false);
		}

		private void OnRoomJoinError (BaseEvent evt) {
			if (Verbose) {
				Debug.LogError ("Room join failed: " + (string)evt.Params ["errorMessage"]);
			}

			var roomIsFull = (short)evt.Params ["errorCode"] == 20;
			((Action<Room, bool>)OnResultDelegate)?.Invoke (null, roomIsFull);
		}

		private void OnRoomCreationError (BaseEvent evt) {
			var errorMessage = (string)evt.Params ["errorMessage"];
			var roomAlreadyExists = (short)evt.Params ["errorCode"] == 12;
			if (roomAlreadyExists) {
				Debug.LogWarning ("SFS Room already exists: " + errorMessage);
			} else {
				Debug.LogError ("SFS Room creation failed: " + errorMessage);
			}
			((Action<Room, bool>)OnResultDelegate)?.Invoke (null, roomAlreadyExists);
		}

		private void _OnObjectMessage (BaseEvent evt) {
			ISFSObject dataObj = (SFSObject)evt.Params ["message"];
			SFSUser sender = (SFSUser)evt.Params ["sender"];

			if (Verbose) {
				Debug.Log ($"Smartfox received object message from {sender.Name}: {dataObj.GetDump ()}");
			}
			OnObjectMessage.Invoke (dataObj, sender);
		}

		private void _OnPrivateMessage (BaseEvent evt) {
			string message = (string)evt.Params ["message"];
			SFSUser sender = (SFSUser)evt.Params ["sender"];

			if (Verbose) {
				Debug.Log ($"Smartfox received private message from {sender.Name}: {message}");
			}
			OnPrivateMessage.Invoke (message, sender);
		}

		/*private void OnUserVariableUpdate (BaseEvent evt) {
			var changedVars = (List<string>)evt.Params ["changedVars"];
			SFSUser user = (SFSUser)evt.Params ["user"];
			if (user == sfs.MySelf) return;
		}*/

		private void OnUserEnterRoom (BaseEvent evt) {
			if (HostUser == null) {
				User user = (User)evt.Params ["user"];
				Room room = (Room)evt.Params ["room"];

				int hostId = room.GetVariable ("HostId").GetIntValue ();
				if (user.Id == hostId) {
					if (Verbose) {
						Debug.Log ("GameHost Joined Room UserId=" + user.Id);
					}
					HostUser = user;
				}
			}
		}

		private void OnUserExitRoom (BaseEvent evt) {
			User user = (User)evt.Params ["user"];
			Room room = (Room)evt.Params ["room"];
			if (user != sfs.MySelf) {
				OnUserLeftRoom?.Invoke (user, room);
			}
		}

		public static void Broadcast (ISFSObject dataObj, bool toPlayers = true, bool toSpectators = false) {
			if (toPlayers && toSpectators) {
				sfs.Send (new ObjectMessageRequest (dataObj, sfs.LastJoinedRoom));
			} else {
				sfs.Send (new ObjectMessageRequest (dataObj, sfs.LastJoinedRoom, toPlayers ? sfs.LastJoinedRoom.PlayerList : sfs.LastJoinedRoom.SpectatorList));
			}
		}

		public static void Send (string s, User user = null) {
			if (sfs != null) {
				if (user == null)
					user = HostUser;
				if (Instance.Verbose) {
					Debug.LogFormat ("Send {0}: {1}", user.Id, s);
				}
				sfs.Send (new PrivateMessageRequest (s, user.Id));
			}
		}

		/// <summary>
        /// Send object message to user. If no user specified, then send to game host
        /// </summary>
		public static void Send (ISFSObject dataObj, User user = null) {
			sfs.Send (new ObjectMessageRequest (dataObj, sfs.LastJoinedRoom, user == null ? HostUserList : new List<User> { user }));
		}

		public static void Send (string varName, bool value) {
			ObjectMessageToHost.PutBool (varName, value);
		}

		public static void Send (string varName, int value) {
			ObjectMessageToHost.PutInt (varName, value);
		}

		public static void Send (string varName, long value) {
			ObjectMessageToHost.PutLong (varName, value);
		}

		public static void Send (string varName, int [] value) {
			ObjectMessageToHost.PutIntArray (varName, value);
		}

		public static void Send (string varName, float value) {
			ObjectMessageToHost.PutFloat (varName, value);
		}

		public static void Send (string varName, float [] value) {
			ObjectMessageToHost.PutFloatArray (varName, value);
		}

		public static void Send (string varName, string value) {
			ObjectMessageToHost.PutUtfString (varName, value);
		}

		public static void Disconnect () {
			try {
				if (Instance != null) {
					Instance.Reset ();
				}
			} catch (Exception ex) {
				Debug.LogError ("Exception while disconnecting smartfox client: " + ex);
			}
		}
	}

}