﻿using Newtonsoft.Json;
using SafeObjectPool;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CSRedis {
	public partial class CSRedisClient : IDisposable {
		/// <summary>
		/// 按 key 规则分区存储
		/// </summary>
		public ConcurrentDictionary<string, RedisClientPool> Nodes { get; } = new ConcurrentDictionary<string, RedisClientPool>();
		private int NodesIndexIncrement = -1;
		private ConcurrentDictionary<int, string> NodesIndex { get; } = new ConcurrentDictionary<int, string>();
		private ConcurrentDictionary<string, int> NodesKey { get; } = new ConcurrentDictionary<string, int>();
		internal Func<string, string> NodeRuleRaw;
		internal Func<string, string> NodeRuleExternal;
		private object NodesLock = new object();
		public ConcurrentDictionary<ushort, ushort> SlotCache = new ConcurrentDictionary<ushort, ushort>();

		private int AutoStartPipeCommitCount { get => Nodes.First().Value.AutoStartPipeCommitCount; set => Nodes.Values.ToList().ForEach(p => p.AutoStartPipeCommitCount = value); }
		private int AutoStartPipeCommitTimeout { get => Nodes.First().Value.AutoStartPipeCommitTimeout; set => Nodes.Values.ToList().ForEach(p => p.AutoStartPipeCommitTimeout = value); }

		private Func<JsonSerializerSettings> JsonSerializerSettings = () => {
			var st = new JsonSerializerSettings();
			st.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
			st.DateFormatHandling = DateFormatHandling.IsoDateFormat;
			st.DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind;
			return st;
		};

		/// <summary>
		/// 自定义序列化方法
		/// </summary>
		public static Func<object, string> Serialize;
		/// <summary>
		/// 自定义反序列化方法
		/// </summary>
		public static Func<string, Type, object> Deserialize;

		DateTime _dt1970 = new DateTime(1970, 1, 1);
		Random _rnd = new Random();

		#region 序列化写入，反序列化
		internal string SerializeObject(object value) {
			if (Serialize != null) return Serialize(value);
			return JsonConvert.SerializeObject(value, this.JsonSerializerSettings());
		}
		internal T DeserializeObject<T>(string value) {
			if (Deserialize != null) return (T)Deserialize(value, typeof(T));
			return JsonConvert.DeserializeObject<T>(value, this.JsonSerializerSettings());
		}

		internal object SerializeRedisValueInternal(object value) {

			if (value == null) return null;
			var type = value.GetType();
			var typename = type.ToString().TrimEnd(']');
			if (typename == "System.Byte[" ||
				typename == "System.String") return value;

			if (type.IsValueType) {
				bool isNullable = typename.StartsWith("System.Nullable`1[");
				var basename = isNullable ? typename.Substring(18) : typename;

				switch (basename) {
					case "System.Boolean": return value.ToString() == "True" ? "1" : "0";
					case "System.Byte": return value.ToString();
					case "System.Char": return value.ToString()[0];
					case "System.Decimal":
					case "System.Double":
					case "System.Single":
					case "System.Int32":
					case "System.Int64":
					case "System.SByte":
					case "System.Int16":
					case "System.UInt32":
					case "System.UInt64":
					case "System.UInt16": return value.ToString();
					case "System.DateTime": return ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:sszzzz", System.Globalization.DateTimeFormatInfo.InvariantInfo);
					case "System.DateTimeOffset": return value.ToString();
					case "System.TimeSpan": return ((TimeSpan)value).Ticks;
					case "System.Guid": return value.ToString();
				}
			}

			return this.SerializeObject(value);
		}
		internal T DeserializeRedisValueInternal<T>(byte[] value) {
			if (value == null) return default(T);
			var type = typeof(T);
			var typename = type.ToString().TrimEnd(']');
			if (typename == "System.Byte[") return (T)Convert.ChangeType(value, type);
			if (typename == "System.String") return (T)Convert.ChangeType(Nodes.First().Value.Encoding.GetString(value), type);
			if (typename == "System.Boolean[") return (T)Convert.ChangeType(value.Select(a => a == 49).ToArray(), type);

			var valueStr = Nodes.First().Value.Encoding.GetString(value);
			if (string.IsNullOrEmpty(valueStr)) return default(T);
			if (type.IsValueType) {
				bool isNullable = typename.StartsWith("System.Nullable`1[");
				var basename = isNullable ? typename.Substring(18) : typename;

				bool isElse = false;
				object obj = null;
				switch (basename) {
					case "System.Boolean":
						if (valueStr == "1") obj = true;
						else if (valueStr == "0") obj = false;
						break;
					case "System.Byte":
						if (byte.TryParse(valueStr, out var trybyte)) obj = trybyte;
						break;
					case "System.Char":
						if (valueStr.Length > 0) obj = valueStr[0];
						break;
					case "System.Decimal":
						if (Decimal.TryParse(valueStr, out var trydec)) obj = trydec;
						break;
					case "System.Double":
						if (Double.TryParse(valueStr, out var trydb)) obj = trydb;
						break;
					case "System.Single":
						if (Single.TryParse(valueStr, out var trysg)) obj = trysg;
						break;
					case "System.Int32":
						if (Int32.TryParse(valueStr, out var tryint32)) obj = tryint32;
						break;
					case "System.Int64":
						if (Int64.TryParse(valueStr, out var tryint64)) obj = tryint64;
						break;
					case "System.SByte":
						if (SByte.TryParse(valueStr, out var trysb)) obj = trysb;
						break;
					case "System.Int16":
						if (Int16.TryParse(valueStr, out var tryint16)) obj = tryint16;
						break;
					case "System.UInt32":
						if (UInt32.TryParse(valueStr, out var tryuint32)) obj = tryuint32;
						break;
					case "System.UInt64":
						if (UInt64.TryParse(valueStr, out var tryuint64)) obj = tryuint64;
						break;
					case "System.UInt16":
						if (UInt16.TryParse(valueStr, out var tryuint16)) obj = tryuint16;
						break;
					case "System.DateTime":
						if (DateTime.TryParse(valueStr, out var trydt)) obj = trydt;
						break;
					case "System.DateTimeOffset":
						if (DateTimeOffset.TryParse(valueStr, out var trydtos)) obj = trydtos;
						break;
					case "System.TimeSpan":
						if (Int64.TryParse(valueStr, out tryint64)) obj = new TimeSpan(tryint64);
						break;
					case "System.Guid":
						if (Guid.TryParse(valueStr, out var tryguid)) obj = tryguid;
						break;
					default:
						isElse = true;
						break;
				}

				if (isElse == false) {
					if (obj == null) return default(T);
					return (T)obj;
					//return (T)Convert.ChangeType(obj, typeof(T));
				}
			}

			return this.DeserializeObject<T>(valueStr);
		}
		internal T[] DeserializeRedisValueArrayInternal<T>(byte[][] value) {
			if (value == null) return null;
			var list = new T[value.Length];
			for (var a = 0; a < value.Length; a++) list[a] = this.DeserializeRedisValueInternal<T>(value[a]);
			return list;
		}
		internal (T1, T2)[] DeserializeRedisValueTuple1Internal<T1, T2>(Tuple<byte[], T2>[] value) {
			if (value == null) return null;
			var list = new(T1, T2)[value.Length];
			for (var a = 0; a < value.Length; a++) list[a] = (this.DeserializeRedisValueInternal<T1>(value[a].Item1), value[a].Item2);
			return list;
		}
		internal (T2, T1)[] DeserializeRedisValueTuple2Internal<T2, T1>(Tuple<T2, byte[]>[] value) {
			if (value == null) return null;
			var list = new(T2, T1)[value.Length];
			for (var a = 0; a < value.Length; a++) list[a] = (value[a].Item1, this.DeserializeRedisValueInternal<T1>(value[a].Item2));
			return list;
		}
		internal Dictionary<TKey, TValue> DeserializeRedisValueDictionaryInternal<TKey, TValue>(Dictionary<TKey, byte[]> value) {
			if (value == null) return null;
			var dic = new Dictionary<TKey, TValue>();
			foreach (var kv in value) dic.Add(kv.Key, this.DeserializeRedisValueInternal<TValue>(kv.Value));
			return dic;
		}
		#endregion

		/// <summary>
		/// 创建redis访问类
		/// </summary>
		/// <param name="connectionString">127.0.0.1[:6379],password=123456,defaultDatabase=13,poolsize=50,ssl=false,writeBuffer=10240,prefix=key前辍</param>
		public CSRedisClient(string connectionString) : this(null, connectionString) { }
		/// <summary>
		/// 创建redis访问分区类，通过 KeyRule 对 key 进行分区，连接对应的 connectionString
		/// </summary>
		/// <param name="NodeRule">按key分区规则，返回值格式：127.0.0.1:6379/13，默认方案(null)：取key哈希与节点数取模</param>
		/// <param name="connectionStrings">127.0.0.1[:6379],password=123456,defaultDatabase=13,poolsize=50,ssl=false,writeBuffer=10240,prefix=key前辍</param>
		public CSRedisClient(Func<string, string> NodeRule, params string[] connectionStrings) {
			this.NodeRuleRaw = key => {
				if (Nodes.Count <= 1) return NodesIndex[0];
				var slot = GetClusterSlot(string.Concat(Nodes.First().Value.Prefix, key)); //redis-cluster 模式，选取第一个 connectionString prefix 前辍求 slot
				if (SlotCache.TryGetValue(slot, out var slotIndex) && NodesIndex.TryGetValue(slotIndex, out var slotKey)) return slotKey; //按上一次 MOVED 记录查找节点
				if (this.NodeRuleExternal == null) {
					var idx = Math.Abs(string.Concat(key).GetHashCode()) % NodesIndex.Count;
					return idx < 0 || idx >= NodesIndex.Count ? NodesIndex[0] : NodesIndex[idx];
				}
				return this.NodeRuleExternal(key);
			};
			this.NodeRuleExternal = NodeRule;
			if (connectionStrings == null || connectionStrings.Any() == false) throw new Exception("Redis ConnectionString 未设置");
			foreach (var connectionString in connectionStrings) {
				var pool = new RedisClientPool("", connectionString, client => { });
				if (Nodes.ContainsKey(pool.Key)) throw new Exception($"Node: {pool.Key} 重复，请检查");
				if (this.TryAddNode(pool.Key, pool) == false) {
					pool.Dispose();
					pool = null;
					throw new Exception($"Node: {pool.Key} 无法添加");
				}
			}
			this.NodesServerManager = new NodesServerManagerProvider(this);
		}
		public void Dispose() {
			foreach (var pool in this.Nodes.Values) pool.Dispose();
		}

		T GetAndExecute<T>(RedisClientPool pool, Func<Object<RedisClient>, T> handler, int jump = 100) {
			Object<RedisClient> obj = null;
			Exception ex = null;
			var redirect = ParseClusterRedirect(null);
			try {
				obj = pool.Get();
				var errtimes = 0;
				while (true) { //因网络出错重试，默认1次
					try {
						var ret = handler(obj);
						return ret;
					} catch (RedisException ex3) {
						redirect = ParseClusterRedirect(ex3); //官方集群跳转
						if (redirect == null || jump <= 0) {
							ex = ex3;
							throw ex;
						}
						break;
					} catch (Exception ex2) {
						ex = ex2;
						if (++errtimes > pool._policy._tryit) throw ex; //重试次数完成
						Console.WriteLine($"tryit ({errtimes}) ...");

						try {
							obj.Value.Ping();
							throw ex; //非网络错误，跳出重试逻辑，抛出异常
						} catch {
							obj.ResetValue();
							obj.Value.Ping(); //此时再报错，说明真的网络问题，抛出异常
						}
					}
				}
			} finally {
				pool.Return(obj, ex);
			}
			var redirectHander = redirect.Value.isMoved ? handler : redirectObj => {
				redirectObj.Value.Call("ASKING");
				return handler(redirectObj);
			};
			return GetAndExecute<T>(GetRedirectPool(redirect.Value, pool), redirectHander, jump - 1);
		}
		bool TryAddNode(string nodeKey, RedisClientPool pool) {
			if (Nodes.TryAdd(nodeKey, pool)) {
				var nodeIndex = Interlocked.Increment(ref NodesIndexIncrement);
				if (NodesIndex.TryAdd(nodeIndex, nodeKey) && NodesKey.TryAdd(nodeKey, nodeIndex)) return true;
				Nodes.TryRemove(nodeKey, out var rempool);
				Interlocked.Decrement(ref NodesIndexIncrement);
			}
			return false;
		}
		RedisClientPool GetRedirectPool((bool isMoved, bool isAsk, ushort slot, string endpoint) redirect, RedisClientPool pool) {
			var nodeKey = $"{redirect.endpoint}/{pool._policy._database}";
			if (Nodes.TryGetValue(nodeKey, out var movedPool) == false) {
				lock (NodesLock) {
					if (Nodes.TryGetValue(nodeKey, out movedPool) == false) {
						var connectionString = $"{redirect.endpoint},password={pool._policy._password},defaultDatabase={pool._policy._database},poolsize={pool._policy.PoolSize},preheat=false,ssl={(pool._policy._ssl ? "true" : "false")},writeBuffer={pool._policy._writebuffer},prefix={pool._policy.Prefix}";
						movedPool = new RedisClientPool("", connectionString, client => { });
						if (this.TryAddNode(nodeKey, movedPool) == false) {
							movedPool.Dispose();
							movedPool = null;
						}
					}
				}
				if (movedPool == null)
					throw new Exception($"{(redirect.isMoved ? "MOVED" : "ASK")} {redirect.slot} {redirect.endpoint}");
			}
			// moved 永久定向，ask 临时性一次定向
			if (redirect.isMoved && NodesKey.TryGetValue(nodeKey, out var nodeIndex2)) {
				SlotCache.AddOrUpdate(redirect.slot, (ushort)nodeIndex2, (oldkey, oldvalue) => (ushort)nodeIndex2);
			}
			return movedPool;
		}
		(bool isMoved, bool isAsk, ushort slot, string endpoint)? ParseClusterRedirect(Exception ex) {
			if (ex == null) return null;
			bool isMoved = ex.Message.StartsWith("MOVED ");
			bool isAsk = ex.Message.StartsWith("ASK ");
			if (isMoved == false && isAsk == false) return null;
			var parts = ex.Message.Split(new[] { ' ' }, 3);
			if (parts.Length != 3 ||
				ushort.TryParse(parts[1], out var slot) == false) return null;
			return (isMoved, isAsk, slot, parts[2]);
		}

		T NodesNotSupport<T>(string[] keys, T defaultValue, Func<Object<RedisClient>, string[], T> callback) {
			if (keys == null || keys.Any() == false) return defaultValue;
			var rules = Nodes.Count > 1 ? keys.Select(a => NodeRuleRaw(a)).Distinct() : new[] { Nodes.FirstOrDefault().Key };
			if (rules.Count() > 1) throw new Exception("由于开启了分区模式，keys 分散在多个节点，无法使用此功能");
			var pool = Nodes.TryGetValue(rules.First(), out var b) ? b : Nodes.First().Value;
			string[] rkeys = new string[keys.Length];
			for (int a = 0; a < keys.Length; a++) rkeys[a] = string.Concat(pool.Prefix, keys[a]);
			if (rkeys.Length == 0) return defaultValue;
			return GetAndExecute(pool, conn => callback(conn, rkeys));
		}
		T NodesNotSupport<T>(string key, Func<Object<RedisClient>, string, T> callback) {
			if (Nodes.Count > 1) throw new Exception("由于开启了分区模式，无法使用此功能");
			return ExecuteScalar<T>(key, callback);
		}

		RedisClientPool GetNodeOrThrowNotFound(string nodeKey) {
			if (Nodes.ContainsKey(nodeKey) == false) throw new Exception($"找不到群集节点：{nodeKey}");
			return Nodes[nodeKey];
		}


		#region 缓存壳
		/// <summary>
		/// 缓存壳
		/// </summary>
		/// <typeparam name="T">缓存类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="timeoutSeconds">缓存秒数</param>
		/// <param name="getData">获取源数据的函数</param>
		/// <returns></returns>
		public T CacheShell<T>(string key, int timeoutSeconds, Func<T> getData) {
			if (timeoutSeconds <= 0) return getData();
			var cacheValue = Get(key);
			if (cacheValue != null) {
				try {
					return this.DeserializeObject<T>(cacheValue);
				} catch {
					Del(key);
					throw;
				}
			}
			var ret = getData();
			Set(key, this.SerializeObject(ret), timeoutSeconds);
			return ret;
		}
		/// <summary>
		/// 缓存壳(哈希表)
		/// </summary>
		/// <typeparam name="T">缓存类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <param name="timeoutSeconds">缓存秒数</param>
		/// <param name="getData">获取源数据的函数</param>
		/// <returns></returns>
		public T CacheShell<T>(string key, string field, int timeoutSeconds, Func<T> getData) {
			if (timeoutSeconds <= 0) return getData();
			var cacheValue = HGet(key, field);
			if (cacheValue != null) {
				try {
					var value = this.DeserializeObject<(T, long)>(cacheValue);
					if (DateTime.Now.Subtract(_dt1970.AddSeconds(value.Item2)).TotalSeconds <= timeoutSeconds) return value.Item1;
				} catch {
					HDel(key, field);
					throw;
				}
			}
			var ret = getData();
			HSet(key, field, this.SerializeObject((ret, (long)DateTime.Now.Subtract(_dt1970).TotalSeconds)));
			return ret;
		}
		/// <summary>
		/// 缓存壳(哈希表)，将 fields 每个元素存储到单独的缓存片，实现最大化复用
		/// </summary>
		/// <typeparam name="T">缓存类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="fields">字段</param>
		/// <param name="timeoutSeconds">缓存秒数</param>
		/// <param name="getData">获取源数据的函数，输入参数是没有缓存的 fields，返回值应该是 (field, value)[]</param>
		/// <returns></returns>
		public T[] CacheShell<T>(string key, string[] fields, int timeoutSeconds, Func<string[], (string, T)[]> getData) {
			fields = fields?.Distinct().ToArray();
			if (fields == null || fields.Length == 0) return new T[0];
			if (timeoutSeconds <= 0) return getData(fields).Select(a => a.Item2).ToArray();

			var ret = new T[fields.Length];
			var cacheValue = HMGet(key, fields);
			var fieldsMGet = new Dictionary<string, int>();

			for (var a = 0; a < ret.Length; a++) {
				if (cacheValue[a] != null) {
					try {
						var value = this.DeserializeObject<(T, long)>(cacheValue[a]);
						if (DateTime.Now.Subtract(_dt1970.AddSeconds(value.Item2)).TotalSeconds <= timeoutSeconds) {
							ret[a] = value.Item1;
							continue;
						}
					} catch {
						HDel(key, fields[a]);
						throw;
					}
				}
				fieldsMGet.Add(fields[a], a);
			}

			if (fieldsMGet.Any()) {
				var getDataIntput = fieldsMGet.Keys.ToArray();
				var data = getData(getDataIntput);
				var mset = new(string field, object value)[fieldsMGet.Count];
				var msetIndex = 0;
				foreach (var d in data) {
					if (fieldsMGet.ContainsKey(d.Item1) == false) throw new Exception($"使用 CacheShell 请确认 getData 返回值 (string, T)[] 中的 Item1 值: {d.Item1} 存在于 输入参数: {string.Join(",", getDataIntput)}");
					ret[fieldsMGet[d.Item1]] = d.Item2;
					mset[msetIndex++] = (d.Item1, this.SerializeObject((d.Item2, (long)DateTime.Now.Subtract(_dt1970).TotalSeconds)));
					fieldsMGet.Remove(d.Item1);
				}
				foreach (var fieldNull in fieldsMGet.Keys) {
					ret[fieldsMGet[fieldNull]] = default(T);
					mset[msetIndex++] = (fieldNull, this.SerializeObject((default(T), (long)DateTime.Now.Subtract(_dt1970).TotalSeconds)));
				}
				if (mset.Any()) HMSet(key, mset);
			}
			return ret;
		}
		#endregion

		#region 分区方式 Execute
		internal T ExecuteScalar<T>(string key, Func<Object<RedisClient>, string, T> hander) {
			if (key == null) return default(T);
			var pool = NodeRuleRaw == null || Nodes.Count == 1 ? Nodes.First().Value : (Nodes.TryGetValue(NodeRuleRaw(key), out var b) ? b : Nodes.First().Value);
			key = string.Concat(pool.Prefix, key);
			return GetAndExecute(pool, conn => hander(conn, key));
		}
		internal T[] ExecuteArray<T>(string[] key, Func<Object<RedisClient>, string[], T[]> hander) {
			if (key == null || key.Any() == false) return new T[0];
			if (NodeRuleRaw == null || Nodes.Count == 1) {
				var pool = Nodes.First().Value;
				var keys = key.Select(a => string.Concat(pool.Prefix, a)).ToArray();
				return GetAndExecute(pool, conn => hander(conn, keys));
			}
			var rules = new Dictionary<string, List<(string, int)>>();
			for (var a = 0; a < key.Length; a++) {
				var rule = NodeRuleRaw(key[a]);
				if (rules.ContainsKey(rule)) rules[rule].Add((key[a], a));
				else rules.Add(rule, new List<(string, int)> { (key[a], a) });
			}
			T[] ret = new T[key.Length];
			foreach (var r in rules) {
				var pool = Nodes.TryGetValue(r.Key, out var b) ? b : Nodes.First().Value;
				var keys = r.Value.Select(a => string.Concat(pool.Prefix, a.Item1)).ToArray();
				GetAndExecute(pool, conn => {
					var vals = hander(conn, keys);
					for (var z = 0; z < r.Value.Count; z++) {
						ret[r.Value[z].Item2] = vals == null || z >= vals.Length ? default(T) : vals[z];
					}
					return 0;
				});
			}
			return ret;
		}
		internal long ExecuteNonQuery(string[] key, Func<Object<RedisClient>, string[], long> hander) {
			if (key == null || key.Any() == false) return 0;
			if (NodeRuleRaw == null || Nodes.Count == 1) {
				var pool = Nodes.First().Value;
				var keys = key.Select(a => string.Concat(pool.Prefix, a)).ToArray();
				return GetAndExecute(pool, conn => hander(conn, keys));
			}
			var rules = new Dictionary<string, List<string>>();
			for (var a = 0; a < key.Length; a++) {
				var rule = NodeRuleRaw(key[a]);
				if (rules.ContainsKey(rule)) rules[rule].Add(key[a]);
				else rules.Add(rule, new List<string> { key[a] });
			}
			long affrows = 0;
			foreach (var r in rules) {
				var pool = Nodes.TryGetValue(r.Key, out var b) ? b : Nodes.First().Value;
				var keys = r.Value.Select(a => string.Concat(pool.Prefix, a)).ToArray();
				affrows += GetAndExecute(pool, conn => hander(conn, keys));
			}
			return affrows;
		}

		#region crc16
		private static readonly ushort[] crc16tab = {
			0x0000,0x1021,0x2042,0x3063,0x4084,0x50a5,0x60c6,0x70e7,
			0x8108,0x9129,0xa14a,0xb16b,0xc18c,0xd1ad,0xe1ce,0xf1ef,
			0x1231,0x0210,0x3273,0x2252,0x52b5,0x4294,0x72f7,0x62d6,
			0x9339,0x8318,0xb37b,0xa35a,0xd3bd,0xc39c,0xf3ff,0xe3de,
			0x2462,0x3443,0x0420,0x1401,0x64e6,0x74c7,0x44a4,0x5485,
			0xa56a,0xb54b,0x8528,0x9509,0xe5ee,0xf5cf,0xc5ac,0xd58d,
			0x3653,0x2672,0x1611,0x0630,0x76d7,0x66f6,0x5695,0x46b4,
			0xb75b,0xa77a,0x9719,0x8738,0xf7df,0xe7fe,0xd79d,0xc7bc,
			0x48c4,0x58e5,0x6886,0x78a7,0x0840,0x1861,0x2802,0x3823,
			0xc9cc,0xd9ed,0xe98e,0xf9af,0x8948,0x9969,0xa90a,0xb92b,
			0x5af5,0x4ad4,0x7ab7,0x6a96,0x1a71,0x0a50,0x3a33,0x2a12,
			0xdbfd,0xcbdc,0xfbbf,0xeb9e,0x9b79,0x8b58,0xbb3b,0xab1a,
			0x6ca6,0x7c87,0x4ce4,0x5cc5,0x2c22,0x3c03,0x0c60,0x1c41,
			0xedae,0xfd8f,0xcdec,0xddcd,0xad2a,0xbd0b,0x8d68,0x9d49,
			0x7e97,0x6eb6,0x5ed5,0x4ef4,0x3e13,0x2e32,0x1e51,0x0e70,
			0xff9f,0xefbe,0xdfdd,0xcffc,0xbf1b,0xaf3a,0x9f59,0x8f78,
			0x9188,0x81a9,0xb1ca,0xa1eb,0xd10c,0xc12d,0xf14e,0xe16f,
			0x1080,0x00a1,0x30c2,0x20e3,0x5004,0x4025,0x7046,0x6067,
			0x83b9,0x9398,0xa3fb,0xb3da,0xc33d,0xd31c,0xe37f,0xf35e,
			0x02b1,0x1290,0x22f3,0x32d2,0x4235,0x5214,0x6277,0x7256,
			0xb5ea,0xa5cb,0x95a8,0x8589,0xf56e,0xe54f,0xd52c,0xc50d,
			0x34e2,0x24c3,0x14a0,0x0481,0x7466,0x6447,0x5424,0x4405,
			0xa7db,0xb7fa,0x8799,0x97b8,0xe75f,0xf77e,0xc71d,0xd73c,
			0x26d3,0x36f2,0x0691,0x16b0,0x6657,0x7676,0x4615,0x5634,
			0xd94c,0xc96d,0xf90e,0xe92f,0x99c8,0x89e9,0xb98a,0xa9ab,
			0x5844,0x4865,0x7806,0x6827,0x18c0,0x08e1,0x3882,0x28a3,
			0xcb7d,0xdb5c,0xeb3f,0xfb1e,0x8bf9,0x9bd8,0xabbb,0xbb9a,
			0x4a75,0x5a54,0x6a37,0x7a16,0x0af1,0x1ad0,0x2ab3,0x3a92,
			0xfd2e,0xed0f,0xdd6c,0xcd4d,0xbdaa,0xad8b,0x9de8,0x8dc9,
			0x7c26,0x6c07,0x5c64,0x4c45,0x3ca2,0x2c83,0x1ce0,0x0cc1,
			0xef1f,0xff3e,0xcf5d,0xdf7c,0xaf9b,0xbfba,0x8fd9,0x9ff8,
			0x6e17,0x7e36,0x4e55,0x5e74,0x2e93,0x3eb2,0x0ed1,0x1ef0
		};
		public static ushort GetClusterSlot(string key) {
			//HASH_SLOT = CRC16(key) mod 16384
			var blob = Encoding.ASCII.GetBytes(key);
			int offset = 0, count = blob.Length, start = -1, end = -1;
			byte lt = (byte)'{', rt = (byte)'}';
			for (int a = 0; a < count - 1; a++)
				if (blob[a] == lt) {
					start = a;
					break;
				}
			if (start >= 0) {
				for (int a = start + 1; a < count; a++)
					if (blob[a] == rt) {
						end = a;
						break;
					}
			}

			if (start >= 0
				&& end >= 0
				&& --end != start) {
				offset = start + 1;
				count = end - start;
			}

			uint crc = 0;
			for (int i = 0; i < count; i++)
				crc = ((crc << 8) ^ crc16tab[((crc >> 8) ^ blob[offset++]) & 0x00FF]) & 0x0000FFFF;
			return (ushort)(crc % 16384);
		}
		#endregion

		#endregion

		/// <summary>
		/// 创建管道传输
		/// </summary>
		/// <param name="handler"></param>
		/// <returns></returns>
		public object[] StartPipe(Action<CSRedisClientPipe<string>> handler) {
			if (handler == null) return new object[0];
			var pipe = new CSRedisClientPipe<string>(this);
			handler(pipe);
			return pipe.EndPipe();
		}

		/// <summary>
		/// 创建管道传输，打包提交如：RedisHelper.StartPipe().Set("a", "1").HSet("b", "f", "2").EndPipe();
		/// </summary>
		/// <returns></returns>
		public CSRedisClientPipe<string> StartPipe() {
			return new CSRedisClientPipe<string>(this);
		}

		#region 服务器命令
		/// <summary>
		/// 在所有分区节点上，执行服务器命令
		/// </summary>
		public NodesServerManagerProvider NodesServerManager { get; set; }
		public partial class NodesServerManagerProvider {
			private CSRedisClient _csredis;

			public NodesServerManagerProvider(CSRedisClient csredis) {
				_csredis = csredis;
			}

			/// <summary>
			/// 异步执行一个 AOF（AppendOnly File） 文件重写操作
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] BgRewriteAof() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.BgRewriteAof()))).ToArray();
			/// <summary>
			/// 在后台异步保存当前数据库的数据到磁盘
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] BgSave() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.BgSave()))).ToArray();
			/// <summary>
			/// 关闭客户端连接
			/// </summary>
			/// <param name="ip">ip</param>
			/// <param name="port">端口</param>
			/// <returns></returns>
			public (string node, string value)[] ClientKill(string ip, int port) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ClientKill(ip, port)))).ToArray();
			/// <summary>
			/// 关闭客户端连接
			/// </summary>
			/// <param name="addr">ip:port</param>
			/// <param name="id">客户唯一标识</param>
			/// <param name="type">类型：normal | slave | pubsub</param>
			/// <param name="skipMe">跳过自己</param>
			/// <returns></returns>
			public (string node, long value)[] ClientKill(string addr = null, string id = null, ClientKillType? type = null, bool? skipMe = null) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ClientKill(addr, id, type?.ToString(), skipMe)))).ToArray();
			/// <summary>
			/// 获取连接到服务器的客户端连接列表
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] ClientList() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ClientList()))).ToArray();
			/// <summary>
			/// 获取连接的名称
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] ClientGetName() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ClientGetName()))).ToArray();
			/// <summary>
			/// 在指定时间内终止运行来自客户端的命令
			/// </summary>
			/// <param name="timeout">阻塞时间</param>
			/// <returns></returns>
			public (string node, string value)[] ClientPause(TimeSpan timeout) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ClientPause(timeout)))).ToArray();
			/// <summary>
			/// 设置当前连接的名称
			/// </summary>
			/// <param name="connectionName">连接名称</param>
			/// <returns></returns>
			public (string node, string value)[] ClientSetName(string connectionName) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ClientSetName(connectionName)))).ToArray();
			/// <summary>
			/// 返回当前服务器时间
			/// </summary>
			/// <returns></returns>
			public (string node, DateTime value)[] Time() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.Time()))).ToArray();
			/// <summary>
			/// 获取指定配置参数的值
			/// </summary>
			/// <param name="parameter">参数</param>
			/// <returns></returns>
			public (string node, Dictionary<string, string> value)[] ConfigGet(string parameter) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ConfigGet(parameter).ToDictionary(z => z.Item1, y => y.Item2)))).ToArray();
			/// <summary>
			/// 对启动 Redis 服务器时所指定的 redis.conf 配置文件进行改写
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] ConfigRewrite() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ConfigRewrite()))).ToArray();
			/// <summary>
			/// 修改 redis 配置参数，无需重启
			/// </summary>
			/// <param name="parameter">参数</param>
			/// <param name="value">值</param>
			/// <returns></returns>
			public (string node, string value)[] ConfigSet(string parameter, string value) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ConfigSet(parameter, value)))).ToArray();
			/// <summary>
			/// 重置 INFO 命令中的某些统计数据
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] ConfigResetStat() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.ConfigResetStat()))).ToArray();
			/// <summary>
			/// 返回当前数据库的 key 的数量
			/// </summary>
			/// <returns></returns>
			public (string node, long value)[] DbSize() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.DbSize()))).ToArray();
			/// <summary>
			/// 让 Redis 服务崩溃
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] DebugSegFault() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.DebugSegFault()))).ToArray();
			/// <summary>
			/// 删除所有数据库的所有key
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] FlushAll() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.FlushAll()))).ToArray();
			/// <summary>
			/// 删除当前数据库的所有key
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] FlushDb() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.FlushDb()))).ToArray();
			/// <summary>
			/// 获取 Redis 服务器的各种信息和统计数值
			/// </summary>
			/// <param name="section">部分(all|default|server|clients|memory|persistence|stats|replication|cpu|commandstats|cluster|keyspace)</param>
			/// <returns></returns>
			public (string node, string value)[] Info(InfoSection? section = null) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.Info(section?.ToString())))).ToArray();
			/// <summary>
			/// 返回最近一次 Redis 成功将数据保存到磁盘上的时间
			/// </summary>
			/// <returns></returns>
			public (string node, DateTime value)[] LastSave() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.LastSave()))).ToArray();
			/// <summary>
			/// 实时打印出 Redis 服务器接收到的命令，调试用
			/// </summary>
			/// <param name="onReceived">接收命令</param>
			/// <returns></returns>
			public (string node, string value)[] Monitor(Action<object, object> onReceived) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => {
				c.Value.MonitorReceived += (s, o) => onReceived?.Invoke(s, o.Message);
				return (a.Key, c.Value.Monitor());
			})).ToArray();
			/// <summary>
			/// 返回主从实例所属的角色
			/// </summary>
			/// <returns></returns>
			public (string node, RedisRole value)[] Role() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.Role()))).ToArray();
			/// <summary>
			/// 同步保存数据到硬盘
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] Save() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.Save()))).ToArray();
			/// <summary>
			/// 异步保存数据到硬盘，并关闭服务器
			/// </summary>
			/// <param name="isSave">是否保存</param>
			/// <returns></returns>
			public (string node, string value)[] Shutdown(bool isSave = true) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.Shutdown(isSave)))).ToArray();
			/// <summary>
			/// 将服务器转变为指定服务器的从属服务器(slave server)，如果当前服务器已经是某个主服务器(master server)的从属服务器，那么执行 SLAVEOF host port 将使当前服务器停止对旧主服务器的同步，丢弃旧数据集，转而开始对新主服务器进行同步。
			/// </summary>
			/// <param name="host">主机</param>
			/// <param name="port">端口</param>
			/// <returns></returns>
			public (string node, string value)[] SlaveOf(string host, int port) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.SlaveOf(host, port)))).ToArray();
			/// <summary>
			/// 从属服务器执行命令 SLAVEOF NO ONE 将使得这个从属服务器关闭复制功能，并从从属服务器转变回主服务器，原来同步所得的数据集不会被丢弃。
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] SlaveOfNoOne() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.SlaveOfNoOne()))).ToArray();
			/// <summary>
			/// 管理 redis 的慢日志，按数量获取
			/// </summary>
			/// <param name="count">数量</param>
			/// <returns></returns>
			public (string node, RedisSlowLogEntry[] value)[] SlowLogGet(long? count = null) => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.SlowLogGet(count)))).ToArray();
			/// <summary>
			/// 管理 redis 的慢日志，总数量
			/// </summary>
			/// <returns></returns>
			public (string node, long value)[] SlowLogLen() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.SlowLogLen()))).ToArray();
			/// <summary>
			/// 管理 redis 的慢日志，清空
			/// </summary>
			/// <returns></returns>
			public (string node, string value)[] SlowLogReset() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.SlowLogReset()))).ToArray();
			/// <summary>
			/// 用于复制功能(replication)的内部命令
			/// </summary>
			/// <returns></returns>
			public (string node, byte[] value)[] Sync() => _csredis.Nodes.Values.Select(a => _csredis.GetAndExecute(a, c => (a.Key, c.Value.Sync()))).ToArray();
		}

		/// <summary>
		/// 在指定分区节点上，执行服务器命令
		/// </summary>
		/// <param name="node">节点</param>
		/// <returns></returns>
		public NodeServerManagerProvider NodeServerManager(string node) => new NodeServerManagerProvider(this, GetNodeOrThrowNotFound(node));
		public partial class NodeServerManagerProvider {
			private CSRedisClient _csredis;
			private RedisClientPool _pool;

			public NodeServerManagerProvider(CSRedisClient csredis, RedisClientPool pool) {
				_csredis = csredis;
				_pool = pool;
			}

			/// <summary>
			/// 异步执行一个 AOF（AppendOnly File） 文件重写操作
			/// </summary>
			/// <returns></returns>
			public string BgRewriteAof() => _csredis.GetAndExecute(_pool, c => c.Value.BgRewriteAof());
			/// <summary>
			/// 在后台异步保存当前数据库的数据到磁盘
			/// </summary>
			/// <returns></returns>
			public string BgSave() => _csredis.GetAndExecute(_pool, c => c.Value.BgSave());
			/// <summary>
			/// 关闭客户端连接
			/// </summary>
			/// <param name="ip">ip</param>
			/// <param name="port">端口</param>
			/// <returns></returns>
			public string ClientKill(string ip, int port) => _csredis.GetAndExecute(_pool, c => c.Value.ClientKill(ip, port));
			/// <summary>
			/// 关闭客户端连接
			/// </summary>
			/// <param name="addr">ip:port</param>
			/// <param name="id">客户唯一标识</param>
			/// <param name="type">类型：normal | slave | pubsub</param>
			/// <param name="skipMe">跳过自己</param>
			/// <returns></returns>
			public long ClientKill(string addr = null, string id = null, ClientKillType? type = null, bool? skipMe = null) => _csredis.GetAndExecute(_pool, c => c.Value.ClientKill(addr, id, type?.ToString(), skipMe));
			public enum ClientKillType { normal, slave, pubsub }
			/// <summary>
			/// 获取连接到服务器的客户端连接列表
			/// </summary>
			/// <returns></returns>
			public string ClientList() => _csredis.GetAndExecute(_pool, c => c.Value.ClientList());
			/// <summary>
			/// 获取连接的名称
			/// </summary>
			/// <returns></returns>
			public string ClientGetName() => _csredis.GetAndExecute(_pool, c => c.Value.ClientGetName());
			/// <summary>
			/// 在指定时间内终止运行来自客户端的命令
			/// </summary>
			/// <param name="timeout">阻塞时间</param>
			/// <returns></returns>
			public string ClientPause(TimeSpan timeout) => _csredis.GetAndExecute(_pool, c => c.Value.ClientPause(timeout));
			/// <summary>
			/// 设置当前连接的名称
			/// </summary>
			/// <param name="connectionName">连接名称</param>
			/// <returns></returns>
			public string ClientSetName(string connectionName) => _csredis.GetAndExecute(_pool, c => c.Value.ClientSetName(connectionName));
			/// <summary>
			/// 返回当前服务器时间
			/// </summary>
			/// <returns></returns>
			public DateTime Time() => _csredis.GetAndExecute(_pool, c => c.Value.Time());
			/// <summary>
			/// 获取指定配置参数的值
			/// </summary>
			/// <param name="parameter">参数</param>
			/// <returns></returns>
			public Dictionary<string, string> ConfigGet(string parameter) => _csredis.GetAndExecute(_pool, c => c.Value.ConfigGet(parameter).ToDictionary(z => z.Item1, y => y.Item2));
			/// <summary>
			/// 对启动 Redis 服务器时所指定的 redis.conf 配置文件进行改写
			/// </summary>
			/// <returns></returns>
			public string ConfigRewrite() => _csredis.GetAndExecute(_pool, c => c.Value.ConfigRewrite());
			/// <summary>
			/// 修改 redis 配置参数，无需重启
			/// </summary>
			/// <param name="parameter">参数</param>
			/// <param name="value">值</param>
			/// <returns></returns>
			public string ConfigSet(string parameter, string value) => _csredis.GetAndExecute(_pool, c => c.Value.ConfigSet(parameter, value));
			/// <summary>
			/// 重置 INFO 命令中的某些统计数据
			/// </summary>
			/// <returns></returns>
			public string ConfigResetStat() => _csredis.GetAndExecute(_pool, c => c.Value.ConfigResetStat());
			/// <summary>
			/// 返回当前数据库的 key 的数量
			/// </summary>
			/// <returns></returns>
			public long DbSize() => _csredis.GetAndExecute(_pool, c => c.Value.DbSize());
			/// <summary>
			/// 让 Redis 服务崩溃
			/// </summary>
			/// <returns></returns>
			public string DebugSegFault() => _csredis.GetAndExecute(_pool, c => c.Value.DebugSegFault());
			/// <summary>
			/// 删除所有数据库的所有key
			/// </summary>
			/// <returns></returns>
			public string FlushAll() => _csredis.GetAndExecute(_pool, c => c.Value.FlushAll());
			/// <summary>
			/// 删除当前数据库的所有key
			/// </summary>
			/// <returns></returns>
			public string FlushDb() => _csredis.GetAndExecute(_pool, c => c.Value.FlushDb());
			/// <summary>
			/// 获取 Redis 服务器的各种信息和统计数值
			/// </summary>
			/// <param name="section">部分(Server | Clients | Memory | Persistence | Stats | Replication | CPU | Keyspace)</param>
			/// <returns></returns>
			public string Info(InfoSection? section = null) => _csredis.GetAndExecute(_pool, c => c.Value.Info(section?.ToString()));
			/// <summary>
			/// 返回最近一次 Redis 成功将数据保存到磁盘上的时间
			/// </summary>
			/// <returns></returns>
			public DateTime LastSave() => _csredis.GetAndExecute(_pool, c => c.Value.LastSave());
			/// <summary>
			/// 实时打印出 Redis 服务器接收到的命令，调试用
			/// </summary>
			/// <param name="onReceived">接收命令</param>
			/// <returns></returns>
			public string Monitor(Action<object, object> onReceived) => _csredis.GetAndExecute(_pool, c => {
				c.Value.MonitorReceived += (s, o) => onReceived?.Invoke(s, o.Message);
				return c.Value.Monitor();
			});
			/// <summary>
			/// 返回主从实例所属的角色
			/// </summary>
			/// <returns></returns>
			public RedisRole Role() => _csredis.GetAndExecute(_pool, c => c.Value.Role());
			/// <summary>
			/// 同步保存数据到硬盘
			/// </summary>
			/// <returns></returns>
			public string Save() => _csredis.GetAndExecute(_pool, c => c.Value.Save());
			/// <summary>
			/// 异步保存数据到硬盘，并关闭服务器
			/// </summary>
			/// <param name="isSave">是否保存</param>
			/// <returns></returns>
			public string Shutdown(bool isSave = true) => _csredis.GetAndExecute(_pool, c => c.Value.Shutdown(isSave));
			/// <summary>
			/// 将服务器转变为指定服务器的从属服务器(slave server)，如果当前服务器已经是某个主服务器(master server)的从属服务器，那么执行 SLAVEOF host port 将使当前服务器停止对旧主服务器的同步，丢弃旧数据集，转而开始对新主服务器进行同步。
			/// </summary>
			/// <param name="host">主机</param>
			/// <param name="port">端口</param>
			/// <returns></returns>
			public string SlaveOf(string host, int port) => _csredis.GetAndExecute(_pool, c => c.Value.SlaveOf(host, port));
			/// <summary>
			/// 从属服务器执行命令 SLAVEOF NO ONE 将使得这个从属服务器关闭复制功能，并从从属服务器转变回主服务器，原来同步所得的数据集不会被丢弃。
			/// </summary>
			/// <returns></returns>
			public string SlaveOfNoOne() => _csredis.GetAndExecute(_pool, c => c.Value.SlaveOfNoOne());
			/// <summary>
			/// 管理 redis 的慢日志，按数量获取
			/// </summary>
			/// <param name="count">数量</param>
			/// <returns></returns>
			public RedisSlowLogEntry[] SlowLogGet(long? count = null) => _csredis.GetAndExecute(_pool, c => c.Value.SlowLogGet(count));
			/// <summary>
			/// 管理 redis 的慢日志，总数量
			/// </summary>
			/// <returns></returns>
			public long SlowLogLen() => _csredis.GetAndExecute(_pool, c => c.Value.SlowLogLen());
			/// <summary>
			/// 管理 redis 的慢日志，清空
			/// </summary>
			/// <returns></returns>
			public string SlowLogReset() => _csredis.GetAndExecute(_pool, c => c.Value.SlowLogReset());
			/// <summary>
			/// 用于复制功能(replication)的内部命令
			/// </summary>
			/// <returns></returns>
			public byte[] Sync() => _csredis.GetAndExecute(_pool, c => c.Value.Sync());
		}
		#endregion

		#region 连接命令
		/// <summary>
		/// 验证密码是否正确
		/// </summary>
		/// <param name="nodeKey">分区key</param>
		/// <param name="password">密码</param>
		/// <returns></returns>
		[Obsolete("不建议手工执行，连接池自己管理最佳")]
		private bool Auth(string nodeKey, string password) => GetAndExecute(GetNodeOrThrowNotFound(nodeKey), c => c.Value.Auth(password)) == "OK";
		/// <summary>
		/// 打印字符串
		/// </summary>
		/// <param name="nodeKey">分区key</param>
		/// <param name="message">消息</param>
		/// <returns></returns>
		public string Echo(string nodeKey, string message) => GetAndExecute(GetNodeOrThrowNotFound(nodeKey), c => c.Value.Echo(message));
		/// <summary>
		/// 打印字符串
		/// </summary>
		/// <param name="message">消息</param>
		/// <returns></returns>
		public string Echo(string message) => GetAndExecute(Nodes.First().Value, c => c.Value.Echo(message));
		/// <summary>
		/// 查看服务是否运行
		/// </summary>
		/// <param name="nodeKey">分区key</param>
		/// <returns></returns>
		public bool Ping(string nodeKey) => GetAndExecute(GetNodeOrThrowNotFound(nodeKey), c => c.Value.Ping()) == "PONG";
		/// <summary>
		/// 查看服务是否运行
		/// </summary>
		/// <returns></returns>
		public bool Ping() => GetAndExecute(Nodes.First().Value, c => c.Value.Ping()) == "PONG";
		/// <summary>
		/// 关闭当前连接
		/// </summary>
		/// <param name="nodeKey">分区key</param>
		/// <returns></returns>
		[Obsolete("不建议手工执行，连接池自己管理最佳")]
		private bool Quit(string nodeKey) => GetAndExecute(GetNodeOrThrowNotFound(nodeKey), c => c.Value.Quit()) == "OK";
		/// <summary>
		/// 切换到指定的数据库
		/// </summary>
		/// <param name="nodeKey">分区key</param>
		/// <param name="index">数据库</param>
		/// <returns></returns>
		[Obsolete("不建议手工执行，连接池所有连接应该指向同一数据库，若手工修改将导致数据的不一致")]
		private bool Select(string nodeKey, int index) => GetAndExecute(GetNodeOrThrowNotFound(nodeKey), c => c.Value.Select(index)) == "OK";
		#endregion

		#region Script
		/// <summary>
		/// 执行脚本
		/// </summary>
		/// <param name="script">Lua 脚本</param>
		/// <param name="key">用于定位分区节点，不含prefix前辍</param>
		/// <param name="args">参数</param>
		/// <returns></returns>
		public object Eval(string script, string key, params object[] args) => ExecuteScalar(key, (c, k) => c.Value.Eval(script, new[] { k }, args?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 执行脚本
		/// </summary>
		/// <param name="sha1">脚本缓存的sha1</param>
		/// <param name="key">用于定位分区节点，不含prefix前辍</param>
		/// <param name="args">参数</param>
		/// <returns></returns>
		public object EvalSHA(string sha1, string key, params object[] args) => ExecuteScalar(key, (c, k) => c.Value.EvalSHA(sha1, new[] { k }, args?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 校验所有分区节点中，脚本是否已经缓存。任何分区节点未缓存sha1，都返回false。
		/// </summary>
		/// <param name="sha1">脚本缓存的sha1</param>
		/// <returns></returns>
		public bool[] ScriptExists(params string[] sha1) => Nodes.Select(a => GetAndExecute(a.Value, c => c.Value.ScriptExists(sha1)?.Where(z => z == false).Any() == false)).ToArray();
		/// <summary>
		/// 清除所有分区节点中，所有 Lua 脚本缓存
		/// </summary>
		public void ScriptFlush() => Nodes.Select(a => GetAndExecute(a.Value, c => c.Value.ScriptFlush()));
		/// <summary>
		/// 杀死所有分区节点中，当前正在运行的 Lua 脚本
		/// </summary>
		public void ScriptKill() => Nodes.Select(a => GetAndExecute(a.Value, c => c.Value.ScriptKill()));
		/// <summary>
		/// 在所有分区节点中，缓存脚本后返回 sha1（同样的脚本在任何服务器，缓存后的 sha1 都是相同的）
		/// </summary>
		/// <param name="script">Lua 脚本</param>
		/// <returns></returns>
		public string ScriptLoad(string script) => Nodes.Select(a => GetAndExecute(a.Value, c => (c.Pool.Policy.Name.ToString(), c.Value.ScriptLoad(script)))).First().Item2;
		#endregion

		#region Pub/Sub
		/// <summary>
		/// 用于将信息发送到指定分区节点的频道，最终消息发布格式：1|message
		/// </summary>
		/// <param name="channel">频道名</param>
		/// <param name="message">消息文本</param>
		/// <returns></returns>
		public long Publish(string channel, string message) {
			var msgid = HIncrBy("csredisclient:Publish:msgid", channel, 1);
			return ExecuteScalar(channel, (c, k) => c.Value.Publish(channel, $"{msgid}|{message}"));
		}
		/// <summary>
		/// 用于将信息发送到指定分区节点的频道，与 Publish 方法不同，不返回消息id头，即 1|
		/// </summary>
		/// <param name="channel">频道名</param>
		/// <param name="message">消息文本</param>
		/// <returns></returns>
		public long PublishNoneMessageId(string channel, string message) => ExecuteScalar(channel, (c, k) => c.Value.Publish(channel, message));
		/// <summary>
		/// 查看所有订阅频道
		/// </summary>
		/// <param name="pattern"></param>
		/// <returns></returns>
		public string[] PubSubChannels(string pattern) {
			var ret = new List<string>();
			Nodes.Values.ToList().ForEach(a => ret.AddRange(GetAndExecute(a, c => c.Value.PubSubChannels(pattern))));
			return ret.ToArray();
		}
		/// <summary>
		/// 查看所有模糊订阅端的数量
		/// </summary>
		/// <returns></returns>
		[Obsolete("分区模式下，其他客户端的模糊订阅可能不会返回")]
		public long PubSubNumPat() => GetAndExecute(Nodes.First().Value, c => c.Value.PubSubNumPat());
		/// <summary>
		/// 查看所有订阅端的数量
		/// </summary>
		/// <param name="channels">频道</param>
		/// <returns></returns>
		[Obsolete("分区模式下，其他客户端的订阅可能不会返回")]
		public Dictionary<string, long> PubSubNumSub(params string[] channels) => ExecuteArray(channels, (c, k) => {
			var prefix = (c.Pool as RedisClientPool).Prefix;
			return c.Value.PubSubNumSub(k.Select(z => string.IsNullOrEmpty(prefix) == false && z.StartsWith(prefix) ? z.Substring(prefix.Length) : z).ToArray());
		}).ToDictionary(z => z.Item1, y => y.Item2);
		/// <summary>
		/// 订阅，根据分区规则返回SubscribeObject，Subscribe(("chan1", msg => Console.WriteLine(msg.Body)), ("chan2", msg => Console.WriteLine(msg.Body)))
		/// </summary>
		/// <param name="channels">频道和接收器</param>
		/// <returns>返回可停止订阅的对象</returns>
		public SubscribeObject Subscribe(params (string, Action<SubscribeMessageEventArgs>)[] channels) {
			var chans = channels.Select(a => a.Item1).Distinct().ToArray();
			var onmessages = channels.ToDictionary(a => a.Item1, b => b.Item2);

			var rules = new Dictionary<string, List<string>>();
			for (var a = 0; a < chans.Length; a++) {
				var rule = NodeRuleRaw(chans[a]);
				if (rules.ContainsKey(rule)) rules[rule].Add(chans[a]);
				else rules.Add(rule, new List<string> { chans[a] });
			}

			List<(string[] keys, Object<RedisClient> conn)> subscrs = new List<(string[] keys, Object<RedisClient> conn)>();
			foreach (var r in rules) {
				var pool = Nodes.TryGetValue(r.Key, out var p) ? p : Nodes.First().Value;
				Task.Run(async () => subscrs.Add((r.Value.ToArray(), await pool.GetAsync()))).Wait();
			}

			var so = new SubscribeObject(this, chans, subscrs.ToArray(), onmessages);
			return so;
		}
		public class SubscribeObject : IDisposable {
			internal CSRedisClient Redis;
			public string[] Channels { get; }
			public (string[] chans, Object<RedisClient> conn)[] Subscrs { get; }
			internal Dictionary<string, Action<SubscribeMessageEventArgs>> OnMessageDic;
			public bool IsUnsubscribed { get; private set; } = true;

			internal SubscribeObject(CSRedisClient redis, string[] channels, (string[] chans, Object<RedisClient> conn)[] subscrs, Dictionary<string, Action<SubscribeMessageEventArgs>> onMessageDic) {
				this.Redis = redis;
				this.Channels = channels;
				this.Subscrs = subscrs;
				this.OnMessageDic = onMessageDic;
				this.IsUnsubscribed = false;

				AppDomain.CurrentDomain.ProcessExit += (s1, e1) => {
					this.Dispose();
				};
				Console.CancelKeyPress += (s1, e1) => {
					this.Dispose();
				};

				foreach (var subscr in this.Subscrs) {
					new Thread(Subscribe).Start(subscr);
				}
			}

			private void Subscribe(object state) {
				var subscr = ((string[] chans, Object<RedisClient> conn))state;
				var pool = subscr.conn.Pool as RedisClientPool;
				var testCSRedis_Subscribe_Keepalive = "0\r\n";// $"CSRedis_Subscribe_Keepalive{Guid.NewGuid().ToString()}";

				EventHandler<RedisSubscriptionReceivedEventArgs> SubscriptionReceived = (a, b) => {
					try {
						if (b.Message.Type == "message" && this.OnMessageDic != null && this.OnMessageDic.TryGetValue(b.Message.Channel, out var action) == true) {
							var msgidIdx = b.Message.Body.IndexOf('|');
							if (msgidIdx != -1 && long.TryParse(b.Message.Body.Substring(0, msgidIdx), out var trylong))
								action(new SubscribeMessageEventArgs {
									MessageId = trylong,
									Body = b.Message.Body.Substring(msgidIdx + 1),
									Channel = b.Message.Channel
								});
							else if (b.Message.Body != testCSRedis_Subscribe_Keepalive)
								action(new SubscribeMessageEventArgs {
									MessageId = 0,
									Body = b.Message.Body,
									Channel = b.Message.Channel
								});
						}
					} catch (Exception ex) {
						var bgcolor = Console.BackgroundColor;
						var forecolor = Console.ForegroundColor;
						Console.BackgroundColor = ConsoleColor.DarkRed;
						Console.ForegroundColor = ConsoleColor.White;
						Console.Write($"订阅方法执行出错【{pool.Key}】(channels:{string.Join(",", Channels)})/(chans:{string.Join(",", subscr.chans)})：{ex.Message}\r\n{ex.StackTrace}");
						Console.BackgroundColor = bgcolor;
						Console.ForegroundColor = forecolor;
						Console.WriteLine();
					}
				};
				subscr.conn.Value.SubscriptionReceived += SubscriptionReceived;

				bool isSubscribeing = false;
				bool isKeepliveReSubscribe = false;
				Timer keeplive = new Timer(state2 => {
					if (isSubscribeing == false) return;
					try {
						foreach (var chan in subscr.chans) {
							if (Redis.PublishNoneMessageId(chan, testCSRedis_Subscribe_Keepalive) <= 0) {
								isKeepliveReSubscribe = true;
								//订阅掉线，重新订阅
								try { subscr.conn.Value.Unsubscribe(); } catch { }
								try { subscr.conn.Value.Quit(); } catch { }
								try { subscr.conn.Value.Socket?.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
							}
						}
					} catch {
					}
				}, null, 60000, 60000);
				while (IsUnsubscribed == false) {
					try {
						subscr.conn.Value.Ping();

						var bgcolor = Console.BackgroundColor;
						var forecolor = Console.ForegroundColor;
						Console.BackgroundColor = ConsoleColor.DarkGreen;
						Console.ForegroundColor = ConsoleColor.White;
						Console.Write($"正在订阅【{pool.Key}】(channels:{string.Join(",", Channels)})/(chans:{string.Join(",", subscr.chans)})");
						Console.BackgroundColor = bgcolor;
						Console.ForegroundColor = forecolor;
						Console.WriteLine();

						isSubscribeing = true;
						isKeepliveReSubscribe = false;
						//SetSocketOption KeepAlive 经测试无效，仍然侍丢失频道
						//subscr.conn.Value.Socket?.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, 60000);
						subscr.conn.Value.Subscribe(subscr.chans);

						if (IsUnsubscribed == false) {
							subscr.conn.ResetValue();
							subscr.conn.Value.SubscriptionReceived += SubscriptionReceived;

							if (isKeepliveReSubscribe == true)
								throw new Exception("每60秒检查发现订阅频道丢失");

							//服务器断开连接 IsConnected == false https://github.com/2881099/csredis/issues/37
							if (subscr.conn.Value.IsConnected == false)
								throw new Exception("redis-server 连接已断开");
						}
					} catch (Exception ex) {
						if (IsUnsubscribed) break;

						var bgcolor = Console.BackgroundColor;
						var forecolor = Console.ForegroundColor;
						Console.BackgroundColor = ConsoleColor.DarkYellow;
						Console.ForegroundColor = ConsoleColor.White;
						Console.Write($"订阅出错【{pool.Key}】(channels:{string.Join(",", Channels)})/(chans:{string.Join(",", subscr.chans)})：{ex.Message}，3秒后重连。。。");
						Console.BackgroundColor = bgcolor;
						Console.ForegroundColor = forecolor;
						Console.WriteLine();
						Thread.CurrentThread.Join(1000 * 3);
					}
				}
				subscr.conn.Value.SubscriptionReceived -= SubscriptionReceived;
				isSubscribeing = false;
				isKeepliveReSubscribe = false;
				try { keeplive.Dispose(); } catch { }
			}

			public void Unsubscribe() {
				this.Dispose();
			}

			~SubscribeObject() {
				this.Dispose();
			}

			public void Dispose() {
				this.IsUnsubscribed = true;
				if (this.Subscrs != null) {
					foreach (var subscr in this.Subscrs) {
						try { subscr.conn.Value.Unsubscribe(); } catch { }
						subscr.conn.Pool.Return(subscr.conn, true);
					}
				}
			}
		}
		public class SubscribeMessageEventArgs {
			/// <summary>
			/// 频道的消息id
			/// </summary>
			public long MessageId { get; set; }
			/// <summary>
			/// 频道
			/// </summary>
			public string Channel { get; set; }
			/// <summary>
			/// 接收到的内容
			/// </summary>
			public string Body { get; set; }
		}
		/// <summary>
		/// 模糊订阅，订阅所有分区节点(同条消息只处理一次），返回SubscribeObject，PSubscribe(new [] { "chan1*", "chan2*" }, msg => Console.WriteLine(msg.Body))
		/// </summary>
		/// <param name="channelPatterns">模糊频道</param>
		/// <param name="pmessage">接收器</param>
		/// <returns>返回可停止模糊订阅的对象</returns>
		public PSubscribeObject PSubscribe(string[] channelPatterns, Action<PSubscribePMessageEventArgs> pmessage) {
			var chans = channelPatterns.Distinct().ToArray();

			List<Object<RedisClient>> redisConnections = new List<Object<RedisClient>>();
			foreach (var pool in Nodes) {
				Task.Run(async () => redisConnections.Add(await pool.Value.GetAsync())).Wait();
			}

			var so = new PSubscribeObject(this, chans, redisConnections.ToArray(), pmessage);
			return so;
		}
		public class PSubscribeObject : IDisposable {
			internal CSRedisClient Redis;
			public string[] Channels { get; }
			internal Action<PSubscribePMessageEventArgs> OnPMessage;
			public Object<RedisClient>[] RedisConnections { get; }
			public bool IsPUnsubscribed { get; private set; } = true;

			internal PSubscribeObject(CSRedisClient redis, string[] channels, Object<RedisClient>[] redisConnections, Action<PSubscribePMessageEventArgs> onPMessage) {
				this.Redis = redis;
				this.Channels = channels;
				this.RedisConnections = redisConnections;
				this.OnPMessage = onPMessage;
				this.IsPUnsubscribed = false;

				AppDomain.CurrentDomain.ProcessExit += (s1, e1) => {
					this.Dispose();
				};
				Console.CancelKeyPress += (s1, e1) => {
					this.Dispose();
				};

				foreach (var conn in this.RedisConnections) {
					new Thread(PSubscribe).Start(conn);
				}
			}

			private void PSubscribe(object state) {
				var conn = (Object<RedisClient>)state;
				var pool = conn.Pool as RedisClientPool;
				var psubscribeKey = string.Join("pSpLiT", Channels);

				EventHandler<RedisSubscriptionReceivedEventArgs> SubscriptionReceived = (a, b) => {
					try {
						if (b.Message.Type == "pmessage" && this.OnPMessage != null) {
							var msgidIdx = b.Message.Body.IndexOf('|');
							if (msgidIdx != -1 && long.TryParse(b.Message.Body.Substring(0, msgidIdx), out var trylong)) {
								var readed = Redis.Eval($@"
ARGV[1] = redis.call('HGET', KEYS[1], '{b.Message.Channel}')
if ARGV[1] ~= ARGV[2] then
  redis.call('HSET', KEYS[1], '{b.Message.Channel}', ARGV[2])
  return 1
end
return 0", $"CSRedisPSubscribe{psubscribeKey}", "", trylong.ToString());
								if (readed?.ToString() == "1")
									this.OnPMessage(new PSubscribePMessageEventArgs {
										Body = b.Message.Body.Substring(msgidIdx + 1),
										Channel = b.Message.Channel,
										MessageId = trylong,
										Pattern = b.Message.Pattern
									});
								//else
								//	Console.WriteLine($"消息被处理过：id:{trylong} channel:{b.Message.Channel} pattern:{b.Message.Pattern} body:{b.Message.Body.Substring(msgidIdx + 1)}");
							} else
								this.OnPMessage(new PSubscribePMessageEventArgs {
									Body = b.Message.Body,
									Channel = b.Message.Channel,
									MessageId = 0,
									Pattern = b.Message.Pattern
								});
						}
					} catch (Exception ex) {
						var bgcolor = Console.BackgroundColor;
						var forecolor = Console.ForegroundColor;
						Console.BackgroundColor = ConsoleColor.DarkRed;
						Console.ForegroundColor = ConsoleColor.White;
						Console.Write($"模糊订阅出错【{pool.Key}】(channels:{string.Join(",", Channels)})：{ex.Message}\r\n{ex.StackTrace}");
						Console.BackgroundColor = bgcolor;
						Console.ForegroundColor = forecolor;
						Console.WriteLine();

					}
				};
				conn.Value.SubscriptionReceived += SubscriptionReceived;

				while (true) {
					try {
						conn.Value.Ping();

						var bgcolor = Console.BackgroundColor;
						var forecolor = Console.ForegroundColor;
						Console.BackgroundColor = ConsoleColor.DarkGreen;
						Console.ForegroundColor = ConsoleColor.White;
						Console.Write($"正在模糊订阅【{pool.Key}】(channels:{string.Join(",", Channels)})");
						Console.BackgroundColor = bgcolor;
						Console.ForegroundColor = forecolor;
						Console.WriteLine();

						conn.Value.Socket?.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, 60000);
						conn.Value.PSubscribe(this.Channels);

						if (IsPUnsubscribed == false) {
							conn.ResetValue();
							conn.Value.SubscriptionReceived += SubscriptionReceived;

							//服务器断开连接 IsConnected == false https://github.com/2881099/csredis/issues/37
							if (conn.Value.IsConnected == false)
								throw new Exception("redis-server 连接已断开");
						}
					} catch (Exception ex) {
						if (IsPUnsubscribed) break;

						var bgcolor = Console.BackgroundColor;
						var forecolor = Console.ForegroundColor;
						Console.BackgroundColor = ConsoleColor.DarkYellow;
						Console.ForegroundColor = ConsoleColor.White;
						Console.Write($"模糊订阅出错【{pool.Key}】(channels:{string.Join(",", Channels)})：{ex.Message}，3秒后重连。。。");
						Console.BackgroundColor = bgcolor;
						Console.ForegroundColor = forecolor;
						Console.WriteLine();
						Thread.CurrentThread.Join(1000 * 3);
					}
				}
			}

			public void PUnsubscribe() {
				this.Dispose();
			}

			~PSubscribeObject() {
				this.Dispose();
			}

			public void Dispose() {
				this.IsPUnsubscribed = true;
				if (this.RedisConnections != null) {
					foreach (var conn in this.RedisConnections) {
						try { conn.Value.PUnsubscribe(); } catch { }
						conn.Pool.Return(conn, true);
					}
				}
			}
		}
		public class PSubscribePMessageEventArgs : SubscribeMessageEventArgs {
			/// <summary>
			/// 匹配模式
			/// </summary>
			public string Pattern { get; set; }
		}
		#endregion

		#region 使用列表现实订阅发布 lpush + blpop
		/// <summary>
		/// 使用lpush + blpop订阅端（多端非争抢模式），都可以收到消息
		/// </summary>
		/// <param name="listKey">list key（不含prefix前辍）</param>
		/// <param name="clientId">订阅端标识，若重复则争抢，若唯一必然收到消息</param>
		/// <param name="onMessage">接收消息委托</param>
		/// <returns></returns>
		public SubscribeListBroadcastObject SubscribeListBroadcast(string listKey, string clientId, Action<string> onMessage) {
			this.HSetNx($"{listKey}_SubscribeListBroadcast", clientId, 1);
			var subobj = new SubscribeListBroadcastObject {
				OnDispose = () => {
					this.HDel($"{listKey}_SubscribeListBroadcast", clientId);
				}
			};
			//订阅其他端转发的消息
			subobj.SubscribeLists.Add(this.SubscribeList($"{listKey}_{clientId}", onMessage));
			//订阅主消息，接收消息后分发
			subobj.SubscribeLists.Add(this.SubscribeList(listKey, msg => {
				try {
					this.HSetNx($"{listKey}_SubscribeListBroadcast", clientId, 1);
					if (msg == null) return;

					var clients = this.HKeys($"{listKey}_SubscribeListBroadcast");
					var pipe = this.StartPipe();
					foreach (var c in clients)
						if (string.Compare(clientId, c, true) != 0) //过滤本端分发
							pipe.LPush($"{listKey}_{c}", msg);
					pipe.EndPipe();
					onMessage?.Invoke(msg);
				} catch (ObjectDisposedException) {
				} catch (Exception ex) {
					var bgcolor = Console.BackgroundColor;
					var forecolor = Console.ForegroundColor;
					Console.BackgroundColor = ConsoleColor.DarkRed;
					Console.ForegroundColor = ConsoleColor.White;
					Console.Write($"列表订阅出错(listKey:{listKey})：{ex.Message}");
					Console.BackgroundColor = bgcolor;
					Console.ForegroundColor = forecolor;
					Console.WriteLine();
				}
			}, true));

			AppDomain.CurrentDomain.ProcessExit += (s1, e1) => {
				subobj.Dispose();
			};
			Console.CancelKeyPress += (s1, e1) => {
				subobj.Dispose();
			};

			return subobj;
		}
		public class SubscribeListBroadcastObject : IDisposable {
			internal Action OnDispose;
			internal List<SubscribeListObject> SubscribeLists = new List<SubscribeListObject>();

			~SubscribeListBroadcastObject() {
				this.Dispose();
			}
			public void Dispose() {
				try { OnDispose?.Invoke(); } catch (ObjectDisposedException) { }
				foreach (var sub in SubscribeLists) sub.Dispose();
			}
		}
		/// <summary>
		/// 使用lpush + blpop订阅端（多端争抢模式），只有一端收到消息
		/// </summary>
		/// <param name="listKey">list key（不含prefix前辍）</param>
		/// <param name="onMessage">接收消息委托</param>
		/// <returns></returns>
		public SubscribeListObject SubscribeList(string listKey, Action<string> onMessage) => SubscribeList(listKey, onMessage, false);
		private SubscribeListObject SubscribeList(string listKey, Action<string> onMessage, bool ignoreEmpty) {
			var subobj = new SubscribeListObject();

			var bgcolor = Console.BackgroundColor;
			var forecolor = Console.ForegroundColor;
			Console.BackgroundColor = ConsoleColor.DarkGreen;
			Console.ForegroundColor = ConsoleColor.White;
			Console.Write($"正在订阅列表(listKey:{listKey})");
			Console.BackgroundColor = bgcolor;
			Console.ForegroundColor = forecolor;
			Console.WriteLine();

			new Thread(() => {
				while (subobj.IsUnsubscribed == false) {
					try {
						var msg = this.BLPop(5, listKey);
						if (ignoreEmpty == true || string.IsNullOrEmpty(msg) == false) {
							onMessage?.Invoke(msg);
						}
					} catch (ObjectDisposedException) {
					} catch (Exception ex) {
						bgcolor = Console.BackgroundColor;
						forecolor = Console.ForegroundColor;
						Console.BackgroundColor = ConsoleColor.DarkRed;
						Console.ForegroundColor = ConsoleColor.White;
						Console.Write($"列表订阅出错(listKey:{listKey})：{ex.Message}");
						Console.BackgroundColor = bgcolor;
						Console.ForegroundColor = forecolor;
						Console.WriteLine();

						Thread.CurrentThread.Join(3000);
					}
				}
			}).Start();

			AppDomain.CurrentDomain.ProcessExit += (s1, e1) => {
				subobj.Dispose();
			};
			Console.CancelKeyPress += (s1, e1) => {
				subobj.Dispose();
			};

			return subobj;
		}
		public class SubscribeListObject : IDisposable {
			internal List<SubscribeListObject> OtherSubs = new List<SubscribeListObject>();
			public bool IsUnsubscribed { get; set; }

			~SubscribeListObject() {
				this.Dispose();
			}
			public void Dispose() {
				this.IsUnsubscribed = true;
				foreach (var sub in OtherSubs) sub.Dispose();
			}
		}
		#endregion

		#region HyperLogLog
		/// <summary>
		/// 添加指定元素到 HyperLogLog
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="elements">元素</param>
		/// <returns></returns>
		public bool PfAdd<T>(string key, params T[] elements) => elements == null || elements.Any() == false ? false : ExecuteScalar(key, (c, k) => c.Value.PfAdd(k, elements?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 返回给定 HyperLogLog 的基数估算值
		/// </summary>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		[Obsolete("分区模式下，若keys分散在多个分区节点时，将报错")]
		public long PfCount(params string[] keys) => NodesNotSupport(keys, 0, (c, k) => c.Value.PfCount(k));
		/// <summary>
		/// 将多个 HyperLogLog 合并为一个 HyperLogLog
		/// </summary>
		/// <param name="destKey">新的 HyperLogLog，不含prefix前辍</param>
		/// <param name="sourceKeys">源 HyperLogLog，不含prefix前辍</param>
		/// <returns></returns>
		[Obsolete("分区模式下，若keys分散在多个分区节点时，将报错")]
		public bool PfMerge(string destKey, params string[] sourceKeys) => NodesNotSupport(new[] { destKey }.Concat(sourceKeys).ToArray(), false, (c, k) => c.Value.PfMerge(k.First(), k.Skip(1).ToArray()) == "OK");
		#endregion

		#region Sorted Set
		/// <summary>
		/// 向有序集合添加一个或多个成员，或者更新已存在成员的分数
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="scoreMembers">一个或多个成员分数</param>
		/// <returns></returns>
		public long ZAdd(string key, params (double, object)[] scoreMembers) => scoreMembers == null || scoreMembers.Any() == false ? 0 :
			ExecuteScalar(key, (c, k) => c.Value.ZAdd(k, scoreMembers.Select(a => new Tuple<double, object>(a.Item1, this.SerializeRedisValueInternal(a.Item2))).ToArray()));
		/// <summary>
		/// 获取有序集合的成员数量
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long ZCard(string key) => ExecuteScalar(key, (c, k) => c.Value.ZCard(k));
		/// <summary>
		/// 计算在有序集合中指定区间分数的成员数量
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <returns></returns>
		public long ZCount(string key, double min, double max) => ExecuteScalar(key, (c, k) => c.Value.ZCount(k, min == double.MinValue ? "-inf" : min.ToString(), max == double.MaxValue ? "+inf" : max.ToString()));
		/// <summary>
		/// 计算在有序集合中指定区间分数的成员数量
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <returns></returns>
		public long ZCount(string key, string min, string max) => ExecuteScalar(key, (c, k) => c.Value.ZCount(k, min, max));
		/// <summary>
		/// 有序集合中对指定成员的分数加上增量 increment
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="memeber">成员</param>
		/// <param name="increment">增量值(默认=1)</param>
		/// <returns></returns>
		public double ZIncrBy(string key, string memeber, double increment = 1) => ExecuteScalar(key, (c, k) => c.Value.ZIncrBy(k, increment, memeber));

		/// <summary>
		/// 计算给定的一个或多个有序集的交集，将结果集存储在新的有序集合 destination 中
		/// </summary>
		/// <param name="destination">新的有序集合，不含prefix前辍</param>
		/// <param name="weights">使用 WEIGHTS 选项，你可以为 每个 给定有序集 分别 指定一个乘法因子。如果没有指定 WEIGHTS 选项，乘法因子默认设置为 1 。</param>
		/// <param name="aggregate">Sum | Min | Max</param>
		/// <param name="keys">一个或多个有序集合，不含prefix前辍</param>
		/// <returns></returns>
		public long ZInterStore(string destination, double[] weights, RedisAggregate aggregate, params string[] keys) {
			if (keys == null || keys.Length == 0) throw new Exception("keys 参数不可为空");
			if (weights != null && weights.Length != keys.Length) throw new Exception("weights 和 keys 参数长度必须相同");
			return NodesNotSupport(new[] { destination }.Concat(keys).ToArray(), 0, (c, k) => c.Value.ZInterStore(k.First(), weights, aggregate, k.Skip(1).ToArray()));
		}

		/// <summary>
		/// 通过索引区间返回有序集合成指定区间内的成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public string[] ZRange(string key, long start, long stop) => ExecuteScalar(key, (c, k) => c.Value.ZRange(k, start, stop, false));
		/// <summary>
		/// 通过索引区间返回有序集合成指定区间内的成员
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public T[] ZRange<T>(string key, long start, long stop) => this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.ZRangeBytes(k, start, stop, false)));
		/// <summary>
		/// 通过索引区间返回有序集合成指定区间内的成员和分数
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public (string member, double score)[] ZRangeWithScores(string key, long start, long stop) => ExecuteScalar(key, (c, k) => c.Value.ZRangeWithScores(k, start, stop)).Select(a => (a.Item1, a.Item2)).ToArray();
		/// <summary>
		/// 通过索引区间返回有序集合成指定区间内的成员和分数
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public (T member, double score)[] ZRangeWithScores<T>(string key, long start, long stop) => this.DeserializeRedisValueTuple1Internal<T, double>(ExecuteScalar(key, (c, k) => c.Value.ZRangeBytesWithScores(k, start, stop)));

		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public string[] ZRangeByScore(string key, double min, double max, long? count = null, long offset = 0) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRangeByScore(k, min == double.MinValue ? "-inf" : min.ToString(), max == double.MaxValue ? "+inf" : max.ToString(), false, offset, count));
		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public T[] ZRangeByScore<T>(string key, double min, double max, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.ZRangeBytesByScore(k, min == double.MinValue ? "-inf" : min.ToString(), max == double.MaxValue ? "+inf" : max.ToString(), false, offset, count)));
		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public string[] ZRangeByScore(string key, string min, string max, long? count = null, long offset = 0) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRangeByScore(k, min, max, false, offset, count));
		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public T[] ZRangeByScore<T>(string key, string min, string max, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.ZRangeBytesByScore(k, min, max, false, offset, count)));

		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员和分数
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (string member, double score)[] ZRangeByScoreWithScores(string key, double min, double max, long? count = null, long offset = 0) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRangeByScoreWithScores(k, min == double.MinValue ? "-inf" : min.ToString(), max == double.MaxValue ? "+inf" : max.ToString(), offset, count).Select(z => (z.Item1, z.Item2)).ToArray());
		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员和分数
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (T member, double score)[] ZRangeByScoreWithScores<T>(string key, double min, double max, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueTuple1Internal<T, double>(ExecuteScalar(key, (c, k) => c.Value.ZRangeBytesByScoreWithScores(k, min == double.MinValue ? "-inf" : min.ToString(), max == double.MaxValue ? "+inf" : max.ToString(), offset, count)));
		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员和分数
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (string member, double score)[] ZRangeByScoreWithScores(string key, string min, string max, long? count = null, long offset = 0) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRangeByScoreWithScores(k, min, max, offset, count).Select(z => (z.Item1, z.Item2)).ToArray());
		/// <summary>
		/// 通过分数返回有序集合指定区间内的成员和分数
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (T member, double score)[] ZRangeByScoreWithScores<T>(string key, string min, string max, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueTuple1Internal<T, double>(ExecuteScalar(key, (c, k) => c.Value.ZRangeBytesByScoreWithScores(k, min, max, offset, count)));

		/// <summary>
		/// 返回有序集合中指定成员的索引
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <returns></returns>
		public long? ZRank(string key, object member) => ExecuteScalar(key, (c, k) => c.Value.ZRank(k, this.SerializeRedisValueInternal(member)));
		/// <summary>
		/// 移除有序集合中的一个或多个成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">一个或多个成员</param>
		/// <returns></returns>
		public long ZRem<T>(string key, params T[] member) => member == null || member.Any() == false ? 0 : ExecuteScalar(key, (c, k) => c.Value.ZRem(k, member?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 移除有序集合中给定的排名区间的所有成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public long ZRemRangeByRank(string key, long start, long stop) => ExecuteScalar(key, (c, k) => c.Value.ZRemRangeByRank(k, start, stop));
		/// <summary>
		/// 移除有序集合中给定的分数区间的所有成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <returns></returns>
		public long ZRemRangeByScore(string key, double min, double max) => ExecuteScalar(key, (c, k) => c.Value.ZRemRangeByScore(k, min == double.MinValue ? "-inf" : min.ToString(), max == double.MaxValue ? "+inf" : max.ToString()));
		/// <summary>
		/// 移除有序集合中给定的分数区间的所有成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <returns></returns>
		public long ZRemRangeByScore(string key, string min, string max) => ExecuteScalar(key, (c, k) => c.Value.ZRemRangeByScore(k, min, max));

		/// <summary>
		/// 返回有序集中指定区间内的成员，通过索引，分数从高到底
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public string[] ZRevRange(string key, long start, long stop) => ExecuteScalar(key, (c, k) => c.Value.ZRevRange(k, start, stop, false));
		/// <summary>
		/// 返回有序集中指定区间内的成员，通过索引，分数从高到底
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public T[] ZRevRange<T>(string key, long start, long stop) => this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.ZRevRangeBytes(k, start, stop, false)));
		/// <summary>
		/// 返回有序集中指定区间内的成员和分数，通过索引，分数从高到底
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public (string member, double score)[] ZRevRangeWithScores(string key, long start, long stop) => ExecuteScalar(key, (c, k) => c.Value.ZRevRangeWithScores(k, start, stop)).Select(a => (a.Item1, a.Item2)).ToArray();
		/// <summary>
		/// 返回有序集中指定区间内的成员和分数，通过索引，分数从高到底
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public (T member, double score)[] ZRevRangeWithScores<T>(string key, long start, long stop) => this.DeserializeRedisValueTuple1Internal<T, double>(ExecuteScalar(key, (c, k) => c.Value.ZRevRangeBytesWithScores(k, start, stop)));

		/// <summary>
		/// 返回有序集中指定分数区间内的成员，分数从高到低排序
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public string[] ZRevRangeByScore(string key, double max, double min, long? count = null, long? offset = 0) => ExecuteScalar(key, (c, k) => c.Value.ZRevRangeByScore(k, max == double.MaxValue ? "+inf" : max.ToString(), min == double.MinValue ? "-inf" : min.ToString(), false, offset, count));
		/// <summary>
		/// 返回有序集中指定分数区间内的成员，分数从高到低排序
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public T[] ZRevRangeByScore<T>(string key, double max, double min, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.ZRevRangeBytesByScore(k, max == double.MaxValue ? "+inf" : max.ToString(), min == double.MinValue ? "-inf" : min.ToString(), false, offset, count)));
		/// <summary>
		/// 返回有序集中指定分数区间内的成员，分数从高到低排序
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public string[] ZRevRangeByScore(string key, string max, string min, long? count = null, long? offset = 0) => ExecuteScalar(key, (c, k) => c.Value.ZRevRangeByScore(k, max, min, false, offset, count));
		/// <summary>
		/// 返回有序集中指定分数区间内的成员，分数从高到低排序
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public T[] ZRevRangeByScore<T>(string key, string max, string min, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.ZRevRangeBytesByScore(k, max, min, false, offset, count)));

		/// <summary>
		/// 返回有序集中指定分数区间内的成员和分数，分数从高到低排序
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (string member, double score)[] ZRevRangeByScoreWithScores(string key, double max, double min, long? count = null, long offset = 0) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRevRangeByScoreWithScores(k, max == double.MaxValue ? "+inf" : max.ToString(), min == double.MinValue ? "-inf" : min.ToString(), offset, count).Select(z => (z.Item1, z.Item2)).ToArray());
		/// <summary>
		/// 返回有序集中指定分数区间内的成员和分数，分数从高到低排序
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 double.MaxValue 10</param>
		/// <param name="min">分数最小值 double.MinValue 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (T member, double score)[] ZRevRangeByScoreWithScores<T>(string key, double max, double min, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueTuple1Internal<T, double>(ExecuteScalar(key, (c, k) => c.Value.ZRevRangeBytesByScoreWithScores(k, max == double.MaxValue ? "+inf" : max.ToString(), min == double.MinValue ? "-inf" : min.ToString(), offset, count)));
		/// <summary>
		/// 返回有序集中指定分数区间内的成员和分数，分数从高到低排序
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (string member, double score)[] ZRevRangeByScoreWithScores(string key, string max, string min, long? count = null, long offset = 0) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRevRangeByScoreWithScores(k, max, min, offset, count).Select(z => (z.Item1, z.Item2)).ToArray());
		/// <summary>
		/// 返回有序集中指定分数区间内的成员和分数，分数从高到低排序
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="max">分数最大值 +inf (10 10</param>
		/// <param name="min">分数最小值 -inf (1 1</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public (T member, double score)[] ZRevRangeByScoreWithScores<T>(string key, string max, string min, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueTuple1Internal<T, double>(ExecuteScalar(key, (c, k) => c.Value.ZRevRangeBytesByScoreWithScores(k, max, min, offset, count)));

		/// <summary>
		/// 返回有序集合中指定成员的排名，有序集成员按分数值递减(从大到小)排序
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <returns></returns>
		public long? ZRevRank(string key, object member) => ExecuteScalar(key, (c, k) => c.Value.ZRevRank(k, this.SerializeRedisValueInternal(member)));
		/// <summary>
		/// 返回有序集中，成员的分数值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <returns></returns>
		public double? ZScore(string key, object member) => ExecuteScalar(key, (c, k) => c.Value.ZScore(k, this.SerializeRedisValueInternal(member)));

		/// <summary>
		/// 计算给定的一个或多个有序集的并集，将结果集存储在新的有序集合 destination 中
		/// </summary>
		/// <param name="destination">新的有序集合，不含prefix前辍</param>
		/// <param name="weights">使用 WEIGHTS 选项，你可以为 每个 给定有序集 分别 指定一个乘法因子。如果没有指定 WEIGHTS 选项，乘法因子默认设置为 1 。</param>
		/// <param name="aggregate">Sum | Min | Max</param>
		/// <param name="keys">一个或多个有序集合，不含prefix前辍</param>
		/// <returns></returns>
		public long ZUnionStore(string destination, double[] weights, RedisAggregate aggregate, params string[] keys) {
			if (keys == null || keys.Length == 0) throw new Exception("keys 参数不可为空");
			if (weights != null && weights.Length != keys.Length) throw new Exception("weights 和 keys 参数长度必须相同");
			return NodesNotSupport(new[] { destination }.Concat(keys).ToArray(), 0, (c, k) => c.Value.ZUnionStore(k.First(), weights, aggregate, k.Skip(1).ToArray()));
		}

		/// <summary>
		/// 迭代有序集合中的元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<(string member, double score)> ZScan(string key, long cursor, string pattern = null, long? count = null) => ExecuteScalar(key, (c, k) => {
			var scan = c.Value.ZScan(k, cursor, pattern, count);
			return new RedisScan<(string, double)>(scan.Cursor, scan.Items.Select(z => (z.Item1, z.Item2)).ToArray());
		});
		/// <summary>
		/// 迭代有序集合中的元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<(T member, double score)> ZScan<T>(string key, long cursor, string pattern = null, long? count = null) => ExecuteScalar(key, (c, k) => {
			var scan = c.Value.ZScanBytes(k, cursor, pattern, count);
			return new RedisScan<(T, double)>(scan.Cursor, this.DeserializeRedisValueTuple1Internal<T, double>(scan.Items));
		});

		/// <summary>
		/// 当有序集合的所有成员都具有相同的分值时，有序集合的元素会根据成员的字典序来进行排序，这个命令可以返回给定的有序集合键 key 中，值介于 min 和 max 之间的成员。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <param name="max">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public string[] ZRangeByLex(string key, string min, string max, long? count = null, long offset = 0) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRangeByLex(k, min, max, offset, count));
		/// <summary>
		/// 当有序集合的所有成员都具有相同的分值时，有序集合的元素会根据成员的字典序来进行排序，这个命令可以返回给定的有序集合键 key 中，值介于 min 和 max 之间的成员。
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <param name="max">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <param name="count">返回多少成员</param>
		/// <param name="offset">返回条件偏移位置</param>
		/// <returns></returns>
		public T[] ZRangeByLex<T>(string key, string min, string max, long? count = null, long offset = 0) =>
			this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.ZRangeBytesByLex(k, min, max, offset, count)));

		/// <summary>
		/// 当有序集合的所有成员都具有相同的分值时，有序集合的元素会根据成员的字典序来进行排序，这个命令可以返回给定的有序集合键 key 中，值介于 min 和 max 之间的成员。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <param name="max">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <returns></returns>
		public long ZRemRangeByLex(string key, string min, string max) =>
			ExecuteScalar(key, (c, k) => c.Value.ZRemRangeByLex(k, min, max));
		/// <summary>
		/// 当有序集合的所有成员都具有相同的分值时，有序集合的元素会根据成员的字典序来进行排序，这个命令可以返回给定的有序集合键 key 中，值介于 min 和 max 之间的成员。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="min">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <param name="max">'(' 表示包含在范围，'[' 表示不包含在范围，'+' 正无穷大，'-' 负无限。 ZRANGEBYLEX zset - + ，命令将返回有序集合中的所有元素</param>
		/// <returns></returns>
		public long ZLexCount(string key, string min, string max) =>
			ExecuteScalar(key, (c, k) => c.Value.ZLexCount(k, min, max));
		#endregion

		#region Set
		/// <summary>
		/// 向集合添加一个或多个成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="members">一个或多个成员</param>
		/// <returns></returns>
		public long SAdd<T>(string key, params T[] members) => members == null || members.Any() == false ? 0 :
			ExecuteScalar(key, (c, k) => c.Value.SAdd(k, members?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 获取集合的成员数
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long SCard(string key) => ExecuteScalar(key, (c, k) => c.Value.SCard(k));
		/// <summary>
		/// 返回给定所有集合的差集
		/// </summary>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public string[] SDiff(params string[] keys) => NodesNotSupport(keys, new string[0], (c, k) => c.Value.SDiff(k));
		/// <summary>
		/// 返回给定所有集合的差集
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public T[] SDiff<T>(params string[] keys) => NodesNotSupport(keys, new T[0], (c, k) => this.DeserializeRedisValueArrayInternal<T>(c.Value.SDiffBytes(k)));
		/// <summary>
		/// 返回给定所有集合的差集并存储在 destination 中
		/// </summary>
		/// <param name="destination">新的无序集合，不含prefix前辍</param>
		/// <param name="keys">一个或多个无序集合，不含prefix前辍</param>
		/// <returns></returns>
		public long SDiffStore(string destination, params string[] keys) => NodesNotSupport(new[] { destination }.Concat(keys).ToArray(), 0, (c, k) => c.Value.SDiffStore(k.First(), k.Skip(1).ToArray()));
		/// <summary>
		/// 返回给定所有集合的交集
		/// </summary>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public string[] SInter(params string[] keys) => NodesNotSupport(keys, new string[0], (c, k) => c.Value.SInter(k));
		/// <summary>
		/// 返回给定所有集合的交集
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public T[] SInter<T>(params string[] keys) => NodesNotSupport(keys, new T[0], (c, k) => this.DeserializeRedisValueArrayInternal<T>(c.Value.SInterBytes(k)));
		/// <summary>
		/// 返回给定所有集合的交集并存储在 destination 中
		/// </summary>
		/// <param name="destination">新的无序集合，不含prefix前辍</param>
		/// <param name="keys">一个或多个无序集合，不含prefix前辍</param>
		/// <returns></returns>
		public long SInterStore(string destination, params string[] keys) => NodesNotSupport(new[] { destination }.Concat(keys).ToArray(), 0, (c, k) => c.Value.SInterStore(k.First(), k.Skip(1).ToArray()));
		/// <summary>
		/// 判断 member 元素是否是集合 key 的成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <returns></returns>
		public bool SIsMember(string key, object member) => ExecuteScalar(key, (c, k) => c.Value.SIsMember(k, this.SerializeRedisValueInternal(member)));
		/// <summary>
		/// 返回集合中的所有成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string[] SMembers(string key) => ExecuteScalar(key, (c, k) => c.Value.SMembers(k));
		/// <summary>
		/// 返回集合中的所有成员
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public T[] SMembers<T>(string key) => this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.SMembersBytes(k)));
		/// <summary>
		/// 将 member 元素从 source 集合移动到 destination 集合
		/// </summary>
		/// <param name="source">无序集合key，不含prefix前辍</param>
		/// <param name="destination">目标无序集合key，不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <returns></returns>
		public bool SMove(string source, string destination, object member) {
			string rule = string.Empty;
			if (Nodes.Count > 1) {
				var rule1 = NodeRuleRaw(source);
				var rule2 = NodeRuleRaw(destination);
				if (rule1 != rule2) {
					if (SRem(source, member) <= 0) return false;
					return SAdd(destination, member) > 0;
				}
				rule = rule1;
			}
			var pool = Nodes.TryGetValue(rule, out var b) ? b : Nodes.First().Value;
			var key1 = string.Concat(pool.Prefix, source);
			var key2 = string.Concat(pool.Prefix, destination);
			return GetAndExecute(pool, conn => conn.Value.SMove(key1, key2, this.SerializeRedisValueInternal(member)));
		}
		/// <summary>
		/// 移除并返回集合中的一个随机元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string SPop(string key) => ExecuteScalar(key, (c, k) => c.Value.SPop(k));
		/// <summary>
		/// 移除并返回集合中的一个随机元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public T SPop<T>(string key) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.SPopBytes(k)));
		/// <summary>
		/// 返回集合中的一个随机元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string SRandMember(string key) => ExecuteScalar(key, (c, k) => c.Value.SRandMember(k));
		/// <summary>
		/// 返回集合中的一个随机元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public T SRandMember<T>(string key) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.SRandMemberBytes(k)));
		/// <summary>
		/// 返回集合中一个或多个随机数的元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="count">返回个数</param>
		/// <returns></returns>
		public string[] SRandMembers(string key, int count = 1) => ExecuteScalar(key, (c, k) => c.Value.SRandMembers(k, count));
		/// <summary>
		/// 返回集合中一个或多个随机数的元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="count">返回个数</param>
		/// <returns></returns>
		public T[] SRandMembers<T>(string key, int count = 1) => this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.SRandMembersBytes(k, count)));
		/// <summary>
		/// 移除集合中一个或多个成员
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="members">一个或多个成员</param>
		/// <returns></returns>
		public long SRem<T>(string key, params T[] members) => members == null || members.Any() == false ? 0 : ExecuteScalar(key, (c, k) => c.Value.SRem(k, members?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 返回所有给定集合的并集
		/// </summary>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public string[] SUnion(params string[] keys) => NodesNotSupport(keys, new string[0], (c, k) => c.Value.SUnion(k));
		/// <summary>
		/// 返回所有给定集合的并集
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public T[] SUnion<T>(params string[] keys) => NodesNotSupport(keys, new T[0], (c, k) => this.DeserializeRedisValueArrayInternal<T>(c.Value.SUnionBytes(k)));
		/// <summary>
		/// 所有给定集合的并集存储在 destination 集合中
		/// </summary>
		/// <param name="destination">新的无序集合，不含prefix前辍</param>
		/// <param name="keys">一个或多个无序集合，不含prefix前辍</param>
		/// <returns></returns>
		public long SUnionStore(string destination, params string[] keys) => NodesNotSupport(new[] { destination }.Concat(keys).ToArray(), 0, (c, k) => c.Value.SUnionStore(k.First(), k.Skip(1).ToArray()));
		/// <summary>
		/// 迭代集合中的元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<string> SScan(string key, long cursor, string pattern = null, long? count = null) => ExecuteScalar(key, (c, k) => c.Value.SScan(k, cursor, pattern, count));
		/// <summary>
		/// 迭代集合中的元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<T> SScan<T>(string key, long cursor, string pattern = null, long? count = null) => ExecuteScalar(key, (c, k) => {
			var scan = c.Value.SScanBytes(k, cursor, pattern, count);
			return new RedisScan<T>(scan.Cursor, this.DeserializeRedisValueArrayInternal<T>(scan.Items));
		});
		#endregion

		#region List
		/// <summary>
		/// 它是 LPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BLPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public (string key, string value)? BLPopWithKey(int timeout, params string[] keys) {
			string[] rkeys = null;
			var tuple = NodesNotSupport(keys, null, (c, k) => c.Value.BLPopWithKey(timeout, rkeys = k));
			if (tuple == null) return null;
			return (rkeys?.Where(b => b == tuple.Item1).First() ?? tuple.Item1, tuple.Item2);
		}
		/// <summary>
		/// 它是 LPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BLPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public (string key, T value)? BLPopWithKey<T>(int timeout, params string[] keys) {
			string[] rkeys = null;
			var tuple = NodesNotSupport(keys, null, (c, k) => c.Value.BLPopBytesWithKey(timeout, rkeys = k));
			if (tuple == null) return null;
			return (rkeys?.Where(b => b == tuple.Item1).First() ?? tuple.Item1, this.DeserializeRedisValueInternal<T>(tuple.Item2));
		}
		/// <summary>
		/// 它是 LPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BLPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public string BLPop(int timeout, params string[] keys) => NodesNotSupport(keys, null, (c, k) => c.Value.BLPop(timeout, k));
		/// <summary>
		/// 它是 LPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BLPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public T BLPop<T>(int timeout, params string[] keys) => this.DeserializeRedisValueInternal<T>(NodesNotSupport(keys, null, (c, k) => c.Value.BLPopBytes(timeout, k)));
		/// <summary>
		/// 它是 RPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BRPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public (string key, string value)? BRPopWithKey(int timeout, params string[] keys) {
			string[] rkeys = null;
			var tuple = NodesNotSupport(keys, null, (c, k) => c.Value.BRPopWithKey(timeout, rkeys = k));
			if (tuple == null) return null;
			return (rkeys?.Where(b => b == tuple.Item1).First() ?? tuple.Item1, tuple.Item2);
		}
		/// <summary>
		/// 它是 RPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BRPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public (string key, T value)? BRPopWithKey<T>(int timeout, params string[] keys) {
			string[] rkeys = null;
			var tuple = NodesNotSupport(keys, null, (c, k) => c.Value.BRPopBytesWithKey(timeout, rkeys = k));
			if (tuple == null) return null;
			return (rkeys?.Where(b => b == tuple.Item1).First() ?? tuple.Item1, this.DeserializeRedisValueInternal<T>(tuple.Item2));
		}
		/// <summary>
		/// 它是 RPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BRPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public string BRPop(int timeout, params string[] keys) => NodesNotSupport(keys, null, (c, k) => c.Value.BRPop(timeout, k));
		/// <summary>
		/// 它是 RPOP 命令的阻塞版本，当给定列表内没有任何元素可供弹出的时候，连接将被 BRPOP 命令阻塞，直到等待超时或发现可弹出元素为止，超时返回null
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="timeout">超时(秒)</param>
		/// <param name="keys">一个或多个列表，不含prefix前辍</param>
		/// <returns></returns>
		public T BRPop<T>(int timeout, params string[] keys) => this.DeserializeRedisValueInternal<T>(NodesNotSupport(keys, null, (c, k) => c.Value.BRPopBytes(timeout, k)));
		/// <summary>
		/// BRPOPLPUSH 是 RPOPLPUSH 的阻塞版本，当给定列表 source 不为空时， BRPOPLPUSH 的表现和 RPOPLPUSH 一样。
		/// 当列表 source 为空时， BRPOPLPUSH 命令将阻塞连接，直到等待超时，或有另一个客户端对 source 执行 LPUSH 或 RPUSH 命令为止。
		/// </summary>
		/// <param name="source">源key，不含prefix前辍</param>
		/// <param name="destination">目标key，不含prefix前辍</param>
		/// <param name="timeout">超时(秒)</param>
		/// <returns></returns>
		public string BRPopLPush(string source, string destination, int timeout) => NodesNotSupport(new[] { source, destination }, null, (c, k) => c.Value.BRPopLPush(k.First(), k.Last(), timeout));
		/// <summary>
		/// BRPOPLPUSH 是 RPOPLPUSH 的阻塞版本，当给定列表 source 不为空时， BRPOPLPUSH 的表现和 RPOPLPUSH 一样。
		/// 当列表 source 为空时， BRPOPLPUSH 命令将阻塞连接，直到等待超时，或有另一个客户端对 source 执行 LPUSH 或 RPUSH 命令为止。
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="source">源key，不含prefix前辍</param>
		/// <param name="destination">目标key，不含prefix前辍</param>
		/// <param name="timeout">超时(秒)</param>
		/// <returns></returns>
		public T BRPopLPush<T>(string source, string destination, int timeout) => this.DeserializeRedisValueInternal<T>(NodesNotSupport(new[] { source, destination }, null, (c, k) => c.Value.BRPopBytesLPush(k.First(), k.Last(), timeout)));
		/// <summary>
		/// 通过索引获取列表中的元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="index">索引</param>
		/// <returns></returns>
		public string LIndex(string key, long index) => ExecuteScalar(key, (c, k) => c.Value.LIndex(k, index));
		/// <summary>
		/// 通过索引获取列表中的元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="index">索引</param>
		/// <returns></returns>
		public T LIndex<T>(string key, long index) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.LIndexBytes(k, index)));
		/// <summary>
		/// 在列表中的元素前面插入元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="pivot">列表的元素</param>
		/// <param name="value">新元素</param>
		/// <returns></returns>
		public long LInsertBefore(string key, object pivot, object value) => ExecuteScalar(key, (c, k) => c.Value.LInsert(k, RedisInsert.Before, pivot, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 在列表中的元素后面插入元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="pivot">列表的元素</param>
		/// <param name="value">新元素</param>
		/// <returns></returns>
		public long LInsertAfter(string key, object pivot, object value) => ExecuteScalar(key, (c, k) => c.Value.LInsert(k, RedisInsert.After, pivot, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 获取列表长度
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long LLen(string key) => ExecuteScalar(key, (c, k) => c.Value.LLen(k));
		/// <summary>
		/// 移出并获取列表的第一个元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string LPop(string key) => ExecuteScalar(key, (c, k) => c.Value.LPop(k));
		/// <summary>
		/// 移出并获取列表的第一个元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public T LPop<T>(string key) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.LPopBytes(k)));
		/// <summary>
		/// 将一个或多个值插入到列表头部
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">一个或多个值</param>
		/// <returns>执行 LPUSH 命令后，列表的长度</returns>
		public long LPush<T>(string key, params T[] value) => value == null || value.Any() == false ? 0 : ExecuteScalar(key, (c, k) => c.Value.LPush(k, value?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 将一个值插入到已存在的列表头部
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">值</param>
		/// <returns>执行 LPUSHX 命令后，列表的长度。</returns>
		public long LPushX(string key, object value) => ExecuteScalar(key, (c, k) => c.Value.LPushX(k, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 获取列表指定范围内的元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public string[] LRange(string key, long start, long stop) => ExecuteScalar(key, (c, k) => c.Value.LRange(k, start, stop));
		/// <summary>
		/// 获取列表指定范围内的元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public T[] LRange<T>(string key, long start, long stop) => this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.LRangeBytes(k, start, stop)));
		/// <summary>
		/// 根据参数 count 的值，移除列表中与参数 value 相等的元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="count">移除的数量，大于0时从表头删除数量count，小于0时从表尾删除数量-count，等于0移除所有</param>
		/// <param name="value">元素</param>
		/// <returns></returns>
		public long LRem(string key, long count, object value) => ExecuteScalar(key, (c, k) => c.Value.LRem(k, count, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 通过索引设置列表元素的值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="index">索引</param>
		/// <param name="value">值</param>
		/// <returns></returns>
		public bool LSet(string key, long index, object value) => ExecuteScalar(key, (c, k) => c.Value.LSet(k, index, this.SerializeRedisValueInternal(value))) == "OK";
		/// <summary>
		/// 对一个列表进行修剪，让列表只保留指定区间内的元素，不在指定区间之内的元素都将被删除
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="stop">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public bool LTrim(string key, long start, long stop) => ExecuteScalar(key, (c, k) => c.Value.LTrim(k, start, stop)) == "OK";
		/// <summary>
		/// 移除并获取列表最后一个元素
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string RPop(string key) => ExecuteScalar(key, (c, k) => c.Value.RPop(k));
		/// <summary>
		/// 移除并获取列表最后一个元素
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public T RPop<T>(string key) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.RPopBytes(k)));
		/// <summary>
		/// 将列表 source 中的最后一个元素(尾元素)弹出，并返回给客户端。
		/// 将 source 弹出的元素插入到列表 destination ，作为 destination 列表的的头元素。
		/// </summary>
		/// <param name="source">源key，不含prefix前辍</param>
		/// <param name="destination">目标key，不含prefix前辍</param>
		/// <returns></returns>
		public string RPopLPush(string source, string destination) => NodesNotSupport(new[] { source, destination }, null, (c, k) => c.Value.RPopLPush(k.First(), k.Last()));
		/// <summary>
		/// 将列表 source 中的最后一个元素(尾元素)弹出，并返回给客户端。
		/// 将 source 弹出的元素插入到列表 destination ，作为 destination 列表的的头元素。
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="source">源key，不含prefix前辍</param>
		/// <param name="destination">目标key，不含prefix前辍</param>
		/// <returns></returns>
		public T RPopLPush<T>(string source, string destination) => this.DeserializeRedisValueInternal<T>(NodesNotSupport(new[] { source, destination }, null, (c, k) => c.Value.RPopBytesLPush(k.First(), k.Last())));
		/// <summary>
		/// 在列表中添加一个或多个值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">一个或多个值</param>
		/// <returns>执行 RPUSH 命令后，列表的长度</returns>
		public long RPush<T>(string key, params T[] value) => value == null || value.Any() == false ? 0 : ExecuteScalar(key, (c, k) => c.Value.RPush(k, value?.Select(z => this.SerializeRedisValueInternal(z)).ToArray()));
		/// <summary>
		/// 为已存在的列表添加值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">一个或多个值</param>
		/// <returns>执行 RPUSHX 命令后，列表的长度</returns>
		public long RPushX(string key, object value) => ExecuteScalar(key, (c, k) => c.Value.RPushX(k, this.SerializeRedisValueInternal(value)));
		#endregion

		#region Hash
		/// <summary>
		/// 删除一个或多个哈希表字段
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="fields">字段</param>
		/// <returns></returns>
		public long HDel(string key, params string[] fields) => fields == null || fields.Any() == false ? 0 : ExecuteScalar(key, (c, k) => c.Value.HDel(k, fields));
		/// <summary>
		/// 查看哈希表 key 中，指定的字段是否存在
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <returns></returns>
		public bool HExists(string key, string field) => ExecuteScalar(key, (c, k) => c.Value.HExists(k, field));
		/// <summary>
		/// 获取存储在哈希表中指定字段的值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <returns></returns>
		public string HGet(string key, string field) => ExecuteScalar(key, (c, k) => c.Value.HGet(k, field));
		/// <summary>
		/// 获取存储在哈希表中指定字段的值
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <returns></returns>
		public T HGet<T>(string key, string field) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.HGetBytes(k, field)));
		/// <summary>
		/// 获取在哈希表中指定 key 的所有字段和值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public Dictionary<string, string> HGetAll(string key) => ExecuteScalar(key, (c, k) => c.Value.HGetAll(k));
		/// <summary>
		/// 获取在哈希表中指定 key 的所有字段和值
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public Dictionary<string, T> HGetAll<T>(string key) => this.DeserializeRedisValueDictionaryInternal<string, T>(ExecuteScalar(key, (c, k) => c.Value.HGetAllBytes(k)));
		/// <summary>
		/// 为哈希表 key 中的指定字段的整数值加上增量 increment
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <param name="value">增量值(默认=1)</param>
		/// <returns></returns>
		public long HIncrBy(string key, string field, long value = 1) => ExecuteScalar(key, (c, k) => c.Value.HIncrBy(k, field, value));
		/// <summary>
		/// 为哈希表 key 中的指定字段的整数值加上增量 increment
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <param name="value">增量值(默认=1)</param>
		/// <returns></returns>
		public double HIncrByFloat(string key, string field, double value = 1) => ExecuteScalar(key, (c, k) => c.Value.HIncrByFloat(k, field, value));
		/// <summary>
		/// 获取所有哈希表中的字段
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string[] HKeys(string key) => ExecuteScalar(key, (c, k) => c.Value.HKeys(k));
		/// <summary>
		/// 获取哈希表中字段的数量
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long HLen(string key) => ExecuteScalar(key, (c, k) => c.Value.HLen(k));
		/// <summary>
		/// 获取存储在哈希表中多个字段的值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="fields">字段</param>
		/// <returns></returns>
		public string[] HMGet(string key, params string[] fields) => fields == null || fields.Any() == false ? new string[0] : ExecuteScalar(key, (c, k) => c.Value.HMGet(k, fields));
		/// <summary>
		/// 获取存储在哈希表中多个字段的值
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="fields">一个或多个字段</param>
		/// <returns></returns>
		public T[] HMGet<T>(string key, params string[] fields) => fields == null || fields.Any() == false ? new T[0] : this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.HMGetBytes(k, fields)));
		/// <summary>
		/// 同时将多个 field-value (域-值)对设置到哈希表 key 中
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="keyValues">key1 value1 [key2 value2]</param>
		/// <returns></returns>
		public bool HMSet(string key, params object[] keyValues) {
			if (keyValues == null || keyValues.Any() == false) return false;
			if (keyValues.Length % 2 != 0) throw new Exception("keyValues 参数是键值对，不应该出现奇数(数量)，请检查使用姿势。");
			var parms = new List<object>();
			for (var a = 0; a < keyValues.Length; a += 2) {
				var k = string.Concat(keyValues[a]);
				var v = keyValues[a + 1];
				if (string.IsNullOrEmpty(k)) throw new Exception("keyValues 参数是键值对，并且 key 不可为空");
				parms.Add(k);
				parms.Add(this.SerializeRedisValueInternal(v));
			}
			return ExecuteScalar(key, (c, k) => c.Value.HMSet(k, parms.ToArray())) == "OK";
		}
		/// <summary>
		/// 将哈希表 key 中的字段 field 的值设为 value
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <param name="value">值</param>
		/// <returns>如果字段是哈希表中的一个新建字段，并且值设置成功，返回true。如果哈希表中域字段已经存在且旧值已被新值覆盖，返回false。</returns>
		public bool HSet(string key, string field, object value) => ExecuteScalar(key, (c, k) => c.Value.HSet(k, field, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 只有在字段 field 不存在时，设置哈希表字段的值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="field">字段</param>
		/// <param name="value">值(string 或 byte[])</param>
		/// <returns></returns>
		public bool HSetNx(string key, string field, object value) => ExecuteScalar(key, (c, k) => c.Value.HSetNx(k, field, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 获取哈希表中所有值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string[] HVals(string key) => ExecuteScalar(key, (c, k) => c.Value.HVals(k));
		/// <summary>
		/// 获取哈希表中所有值
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public T[] HVals<T>(string key) => this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.HValsBytes(k)));
		/// <summary>
		/// 迭代哈希表中的键值对
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<(string field, string value)> HScan(string key, long cursor, string pattern = null, long? count = null) => ExecuteScalar(key, (c, k) => {
			var scan = c.Value.HScan(k, cursor, pattern, count);
			return new RedisScan<(string, string)>(scan.Cursor, scan.Items.Select(z => (z.Item1, z.Item2)).ToArray());
		});
		/// <summary>
		/// 迭代哈希表中的键值对
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<(string field, T value)> HScan<T>(string key, long cursor, string pattern = null, long? count = null) => ExecuteScalar(key, (c, k) => {
			var scan = c.Value.HScanBytes(k, cursor, pattern, count);
			return new RedisScan<(string, T)>(scan.Cursor, scan.Items.Select(z => (z.Item1, this.DeserializeRedisValueInternal<T>(z.Item2))).ToArray());
		});
		#endregion

		#region String
		/// <summary>
		/// 如果 key 已经存在并且是一个字符串， APPEND 命令将指定的 value 追加到该 key 原来值（value）的末尾
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">字符串</param>
		/// <returns>追加指定值之后， key 中字符串的长度</returns>
		public long Append(string key, object value) => ExecuteScalar(key, (c, k) => c.Value.Append(k, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 计算给定位置被设置为 1 的比特位的数量
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置</param>
		/// <param name="end">结束位置</param>
		/// <returns></returns>
		public long BitCount(string key, long start, long end) => ExecuteScalar(key, (c, k) => c.Value.BitCount(k, start, end));
		/// <summary>
		/// 对一个或多个保存二进制位的字符串 key 进行位元操作，并将结果保存到 destkey 上
		/// </summary>
		/// <param name="op">And | Or | XOr | Not</param>
		/// <param name="destKey">不含prefix前辍</param>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns>保存到 destkey 的长度，和输入 key 中最长的长度相等</returns>
		public long BitOp(RedisBitOp op, string destKey, params string[] keys) {
			if (string.IsNullOrEmpty(destKey)) throw new Exception("destKey 不能为空");
			if (keys == null || keys.Length == 0) throw new Exception("keys 不能为空");
			return NodesNotSupport(new[] { destKey }.Concat(keys).ToArray(), 0, (c, k) => c.Value.BitOp(op, k.First(), k.Skip(1).ToArray()));
		}
		/// <summary>
		/// 对 key 所储存的值，查找范围内第一个被设置为1或者0的bit位
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="bit">查找值</param>
		/// <param name="start">开始位置，-1是最后一个，-2是倒数第二个</param>
		/// <param name="end">结果位置，-1是最后一个，-2是倒数第二个</param>
		/// <returns>返回范围内第一个被设置为1或者0的bit位</returns>
		public long BitPos(string key, bool bit, long? start = null, long? end = null) => ExecuteScalar(key, (c, k) => c.Value.BitPos(k, bit, start, end));
		/// <summary>
		/// 获取指定 key 的值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string Get(string key) => ExecuteScalar(key, (c, k) => c.Value.Get(k));
		/// <summary>
		/// 获取指定 key 的值
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public T Get<T>(string key) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.GetBytes(k)));
		/// <summary>
		/// 对 key 所储存的值，获取指定偏移量上的位(bit)
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="offset">偏移量</param>
		/// <returns></returns>
		public bool GetBit(string key, uint offset) => ExecuteScalar(key, (c, k) => c.Value.GetBit(k, offset));
		/// <summary>
		/// 返回 key 中字符串值的子字符
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="end">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public string GetRange(string key, long start, long end) => ExecuteScalar(key, (c, k) => c.Value.GetRange(k, start, end));
		/// <summary>
		/// 返回 key 中字符串值的子字符
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="start">开始位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <param name="end">结束位置，0表示第一个元素，-1表示最后一个元素</param>
		/// <returns></returns>
		public T GetRange<T>(string key, long start, long end) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.GetRangeBytes(k, start, end)));
		/// <summary>
		/// 将给定 key 的值设为 value ，并返回 key 的旧值(old value)
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">值</param>
		/// <returns></returns>
		public string GetSet(string key, object value) => ExecuteScalar(key, (c, k) => c.Value.GetSet(k, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 将给定 key 的值设为 value ，并返回 key 的旧值(old value)
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">值</param>
		/// <returns></returns>
		public T GetSet<T>(string key, object value) => this.DeserializeRedisValueInternal<T>(ExecuteScalar(key, (c, k) => c.Value.GetSetBytes(k, this.SerializeRedisValueInternal(value))));
		/// <summary>
		/// 将 key 所储存的值加上给定的增量值（increment）
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">增量值(默认=1)</param>
		/// <returns></returns>
		public long IncrBy(string key, long value = 1) => ExecuteScalar(key, (c, k) => c.Value.IncrBy(k, value));
		/// <summary>
		/// 将 key 所储存的值加上给定的浮点增量值（increment）
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">增量值(默认=1)</param>
		/// <returns></returns>
		public double IncrByFloat(string key, double value = 1) => ExecuteScalar(key, (c, k) => c.Value.IncrByFloat(k, value));
		/// <summary>
		/// 获取多个指定 key 的值(数组)
		/// </summary>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public string[] MGet(params string[] keys) => ExecuteArray(keys, (c, k) => c.Value.MGet(k));
		/// <summary>
		/// 获取多个指定 key 的值(数组)
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="keys">不含prefix前辍</param>
		/// <returns></returns>
		public T[] MGet<T>(params string[] keys) => this.DeserializeRedisValueArrayInternal<T>(ExecuteArray(keys, (c, k) => c.Value.MGetBytes(k)));
		/// <summary>
		/// 同时设置一个或多个 key-value 对
		/// </summary>
		/// <param name="keyValues">key1 value1 [key2 value2]</param>
		/// <returns></returns>
		public bool MSet(params object[] keyValues) => MSetInternal(RedisExistence.Xx, keyValues);
		/// <summary>
		/// 同时设置一个或多个 key-value 对，当且仅当所有给定 key 都不存在
		/// </summary>
		/// <param name="keyValues">key1 value1 [key2 value2]</param>
		/// <returns></returns>
		public bool MSetNx(params object[] keyValues) => MSetInternal(RedisExistence.Nx, keyValues);
		internal bool MSetInternal(RedisExistence exists, params object[] keyValues) {
			if (keyValues == null || keyValues.Any() == false) return false;
			if (keyValues.Length % 2 != 0) throw new Exception("keyValues 参数是键值对，不应该出现奇数(数量)，请检查使用姿势。");
			var dic = new Dictionary<string, object>();
			for (var a = 0; a < keyValues.Length; a += 2) {
				var k = string.Concat(keyValues[a]);
				var v = keyValues[a + 1];
				if (string.IsNullOrEmpty(k)) throw new Exception("keyValues 参数是键值对，并且 key 不可为空");
				if (dic.ContainsKey(k)) dic[k] = v;
				else dic.Add(k, v);
			}
			Func<Object<RedisClient>, string[], long> handle = (c, k) => {
				var prefix = (c.Pool as RedisClientPool)?.Prefix;
				var parms = new object[k.Length * 2];
				for (var a = 0; a < k.Length; a++) {
					parms[a * 2] = k[a];
					parms[a * 2 + 1] = this.SerializeRedisValueInternal(dic[string.IsNullOrEmpty(prefix) ? k[a] : k[a].Substring(prefix.Length)]);
				}
				if (exists == RedisExistence.Nx) return c.Value.MSetNx(parms) ? 1 : 0;
				return c.Value.MSet(parms) == "OK" ? 1 : 0;
			};
			if (exists == RedisExistence.Nx) return NodesNotSupport(dic.Keys.ToArray(), 0, handle) > 0;
			return ExecuteNonQuery(dic.Keys.ToArray(), handle) > 0;
		}
		/// <summary>
		/// 设置指定 key 的值，所有写入参数object都支持string | byte[] | 数值 | 对象
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">值</param>
		/// <param name="expireSeconds">过期(秒单位)</param>
		/// <param name="exists">Nx, Xx</param>
		/// <returns></returns>
		public bool Set(string key, object value, int expireSeconds = -1, RedisExistence? exists = null) {
			object redisValule = this.SerializeRedisValueInternal(value);
			if (expireSeconds <= 0 && exists == null) return ExecuteScalar(key, (c, k) => c.Value.Set(k, redisValule)) == "OK";
			if (expireSeconds <= 0 && exists != null) return ExecuteScalar(key, (c, k) => c.Value.Set(k, redisValule, null, exists)) == "OK";
			if (expireSeconds > 0 && exists == null) return ExecuteScalar(key, (c, k) => c.Value.Set(k, redisValule, expireSeconds, null)) == "OK";
			if (expireSeconds > 0 && exists != null) return ExecuteScalar(key, (c, k) => c.Value.Set(k, redisValule, expireSeconds, exists)) == "OK";
			return false;
		}
		/// <summary>
		/// 对 key 所储存的字符串值，设置或清除指定偏移量上的位(bit)
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="offset">偏移量</param>
		/// <param name="value">值</param>
		/// <returns></returns>
		public bool SetBit(string key, uint offset, bool value) => ExecuteScalar(key, (c, k) => c.Value.SetBit(k, offset, value));
		/// <summary>
		/// 只有在 key 不存在时设置 key 的值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="value">值</param>
		/// <returns></returns>
		public bool SetNx(string key, object value) => ExecuteScalar(key, (c, k) => c.Value.SetNx(k, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 用 value 参数覆写给定 key 所储存的字符串值，从偏移量 offset 开始
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="offset">偏移量</param>
		/// <param name="value">值</param>
		/// <returns>被修改后的字符串长度</returns>
		public long SetRange(string key, uint offset, object value) => ExecuteScalar(key, (c, k) => c.Value.SetRange(k, offset, this.SerializeRedisValueInternal(value)));
		/// <summary>
		/// 返回 key 所储存的字符串值的长度
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long StrLen(string key) => ExecuteScalar(key, (c, k) => c.Value.StrLen(k));
		#endregion

		#region Key
		/// <summary>
		/// 用于在 key 存在时删除 key
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long Del(params string[] key) => ExecuteNonQuery(key, (c, k) => c.Value.Del(k));
		/// <summary>
		/// 序列化给定 key ，并返回被序列化的值
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public byte[] Dump(string key) => ExecuteScalar(key, (c, k) => c.Value.Dump(k));
		/// <summary>
		/// 检查给定 key 是否存在
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public bool Exists(string key) => ExecuteScalar(key, (c, k) => c.Value.Exists(k));
		/// <summary>
		/// 为给定 key 设置过期时间
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="seconds">过期秒数</param>
		/// <returns></returns>
		public bool Expire(string key, int seconds) => ExecuteScalar(key, (c, k) => c.Value.Expire(k, seconds));
		/// <summary>
		/// 为给定 key 设置过期时间
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="expire">过期时间</param>
		/// <returns></returns>
		public bool Expire(string key, TimeSpan expire) => ExecuteScalar(key, (c, k) => c.Value.Expire(k, expire));
		/// <summary>
		/// 为给定 key 设置过期时间
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="expire">过期时间</param>
		/// <returns></returns>
		public bool ExpireAt(string key, DateTime expire) => ExecuteScalar(key, (c, k) => c.Value.ExpireAt(k, expire));
		/// <summary>
		/// 查找所有分区节点中符合给定模式(pattern)的 key
		/// </summary>
		/// <param name="pattern">如：runoob*</param>
		/// <returns></returns>
		public string[] Keys(string pattern) {
			List<string> ret = new List<string>();
			foreach (var pool in Nodes)
				ret.AddRange(GetAndExecute(pool.Value, conn => conn.Value.Keys(pattern)));
			return ret.ToArray();
		}
		/// <summary>
		/// 将当前数据库的 key 移动到给定的数据库 db 当中
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="database">数据库</param>
		/// <returns></returns>
		public bool Move(string key, int database) => ExecuteScalar(key, (c, k) => c.Value.Move(k, database));
		/// <summary>
		/// 该返回给定 key 锁储存的值所使用的内部表示(representation)
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public string ObjectEncoding(string key) => ExecuteScalar(key, (c, k) => c.Value.ObjectEncoding(k));
		/// <summary>
		/// 该返回给定 key 引用所储存的值的次数。此命令主要用于除错
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long? ObjectRefCount(string key) => ExecuteScalar(key, (c, k) => c.Value.Object(RedisObjectSubCommand.RefCount, k));
		/// <summary>
		/// 返回给定 key 自储存以来的空转时间(idle， 没有被读取也没有被写入)，以秒为单位
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long? ObjectIdleTime(string key) => ExecuteScalar(key, (c, k) => c.Value.Object(RedisObjectSubCommand.IdleTime, k));
		/// <summary>
		/// 移除 key 的过期时间，key 将持久保持
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public bool Persist(string key) => ExecuteScalar(key, (c, k) => c.Value.Persist(k));
		/// <summary>
		/// 为给定 key 设置过期时间（毫秒）
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="milliseconds">过期毫秒数</param>
		/// <returns></returns>
		public bool PExpire(string key, int milliseconds) => ExecuteScalar(key, (c, k) => c.Value.PExpire(k, milliseconds));
		/// <summary>
		/// 为给定 key 设置过期时间（毫秒）
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="expire">过期时间</param>
		/// <returns></returns>
		public bool PExpire(string key, TimeSpan expire) => ExecuteScalar(key, (c, k) => c.Value.PExpire(k, expire));
		/// <summary>
		/// 为给定 key 设置过期时间（毫秒）
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="expire">过期时间</param>
		/// <returns></returns>
		public bool PExpireAt(string key, DateTime expire) => ExecuteScalar(key, (c, k) => c.Value.PExpireAt(k, expire));
		/// <summary>
		/// 以毫秒为单位返回 key 的剩余的过期时间
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long PTtl(string key) => ExecuteScalar(key, (c, k) => c.Value.PTtl(k));
		/// <summary>
		/// 从所有节点中随机返回一个 key
		/// </summary>
		/// <returns>返回的 key 如果包含 prefix前辍，则会去除后返回</returns>
		public string RandomKey() => GetAndExecute(Nodes[NodesIndex[_rnd.Next(0, NodesIndex.Count)]], c => {
			var rk = c.Value.RandomKey();
			var prefix = (c.Pool as RedisClientPool).Prefix;
			if (string.IsNullOrEmpty(prefix) == false && rk.StartsWith(prefix)) return rk.Substring(prefix.Length);
			return rk;
		});
		/// <summary>
		/// 修改 key 的名称
		/// </summary>
		/// <param name="key">旧名称，不含prefix前辍</param>
		/// <param name="newKey">新名称，不含prefix前辍</param>
		/// <returns></returns>
		public bool Rename(string key, string newKey) {
			string rule = string.Empty;
			if (Nodes.Count > 1) {
				var rule1 = NodeRuleRaw(key);
				var rule2 = NodeRuleRaw(newKey);
				if (rule1 != rule2) {
					var ret = StartPipe(a => a.Dump(key).Del(key));
					int.TryParse(ret[1]?.ToString(), out var tryint);
					if (ret[0] == null || tryint <= 0) return false;
					return Restore(newKey, (byte[])ret[0]);
				}
				rule = rule1;
			}
			var pool = Nodes.TryGetValue(rule, out var b) ? b : Nodes.First().Value;
			var key1 = string.Concat(pool.Prefix, key);
			var key2 = string.Concat(pool.Prefix, newKey);
			return GetAndExecute(pool, conn => conn.Value.Rename(key1, key2)) == "OK";
		}
		/// <summary>
		/// 修改 key 的名称
		/// </summary>
		/// <param name="key">旧名称，不含prefix前辍</param>
		/// <param name="newKey">新名称，不含prefix前辍</param>
		/// <returns></returns>
		public bool RenameNx(string key, string newKey) => NodesNotSupport(new[] { key, newKey }, false, (c, k) => c.Value.RenameNx(k.First(), k.Last()));
		/// <summary>
		/// 反序列化给定的序列化值，并将它和给定的 key 关联
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="serializedValue">序列化值</param>
		/// <returns></returns>
		public bool Restore(string key, byte[] serializedValue) => ExecuteScalar(key, (c, k) => c.Value.Restore(k, 0, serializedValue)) == "OK";
		/// <summary>
		/// 反序列化给定的序列化值，并将它和给定的 key 关联
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="ttlMilliseconds">毫秒为单位为 key 设置生存时间</param>
		/// <param name="serializedValue">序列化值</param>
		/// <returns></returns>
		public bool Restore(string key, long ttlMilliseconds, byte[] serializedValue) => ExecuteScalar(key, (c, k) => c.Value.Restore(k, ttlMilliseconds, serializedValue)) == "OK";
		/// <summary>
		/// 返回给定列表、集合、有序集合 key 中经过排序的元素，参数资料：http://doc.redisfans.com/key/sort.html
		/// </summary>
		/// <param name="key">列表、集合、有序集合，不含prefix前辍</param>
		/// <param name="count">数量</param>
		/// <param name="offset">偏移量</param>
		/// <param name="by">排序字段</param>
		/// <param name="dir">排序方式</param>
		/// <param name="isAlpha">对字符串或数字进行排序</param>
		/// <param name="get">根据排序的结果来取出相应的键值</param>
		/// <returns></returns>
		public string[] Sort(string key, long? count = null, long offset = 0, string by = null, RedisSortDir? dir = null, bool? isAlpha = null, params string[] get) =>
			NodesNotSupport(key, (c, k) => c.Value.Sort(k, offset, count, by, dir, isAlpha, get));
		/// <summary>
		/// 保存给定列表、集合、有序集合 key 中经过排序的元素，参数资料：http://doc.redisfans.com/key/sort.html
		/// </summary>
		/// <param name="key">列表、集合、有序集合，不含prefix前辍</param>
		/// <param name="destination">目标key，不含prefix前辍</param>
		/// <param name="count">数量</param>
		/// <param name="offset">偏移量</param>
		/// <param name="by">排序字段</param>
		/// <param name="dir">排序方式</param>
		/// <param name="isAlpha">对字符串或数字进行排序</param>
		/// <param name="get">根据排序的结果来取出相应的键值</param>
		/// <returns></returns>
		public long SortAndStore(string key, string destination, long? count = null, long offset = 0, string by = null, RedisSortDir? dir = null, bool? isAlpha = null, params string[] get) =>
			NodesNotSupport(key, (c, k) => c.Value.SortAndStore(k, (c.Pool as RedisClientPool)?.Prefix + destination, offset, count, by, dir, isAlpha, get));
		/// <summary>
		/// 以秒为单位，返回给定 key 的剩余生存时间
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public long Ttl(string key) => ExecuteScalar(key, (c, k) => c.Value.Ttl(k));
		/// <summary>
		/// 返回 key 所储存的值的类型
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <returns></returns>
		public KeyType Type(string key) => Enum.TryParse(ExecuteScalar(key, (c, k) => c.Value.Type(k)), true, out KeyType tryenum) ? tryenum : KeyType.None;
		/// <summary>
		/// 迭代当前数据库中的数据库键
		/// </summary>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<string> Scan(long cursor, string pattern = null, long? count = null) => NodesNotSupport("Scan", (c, k) => c.Value.Scan(cursor, pattern, count));
		/// <summary>
		/// 迭代当前数据库中的数据库键
		/// </summary>
		/// <typeparam name="T">byte[] 或其他类型</typeparam>
		/// <param name="cursor">位置</param>
		/// <param name="pattern">模式</param>
		/// <param name="count">数量</param>
		/// <returns></returns>
		public RedisScan<T> Scan<T>(long cursor, string pattern = null, long? count = null) => NodesNotSupport("Scan<T>", (c, k) => {
			var scan = c.Value.ScanBytes(cursor, pattern, count);
			return new RedisScan<T>(scan.Cursor, this.DeserializeRedisValueArrayInternal<T>(scan.Items));
		});
		#endregion

		#region Geo redis-server 3.2
		/// <summary>
		/// 将指定的地理空间位置（纬度、经度、成员）添加到指定的key中。这些数据将会存储到sorted set这样的目的是为了方便使用GEORADIUS或者GEORADIUSBYMEMBER命令对数据进行半径查询等操作。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="member">成员</param>
		/// <returns>是否成功</returns>
		public bool GeoAdd(string key, double longitude, double latitude, object member) => GeoAdd(key, (longitude, latitude, member)) == 1;
		/// <summary>
		/// 将指定的地理空间位置（纬度、经度、成员）添加到指定的key中。这些数据将会存储到sorted set这样的目的是为了方便使用GEORADIUS或者GEORADIUSBYMEMBER命令对数据进行半径查询等操作。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="values">批量添加的值</param>
		/// <returns>添加到sorted set元素的数目，但不包括已更新score的元素。</returns>
		public long GeoAdd(string key, params (double longitude, double latitude, object member)[] values) => ExecuteScalar(key, (c, k) => c.Value.GeoAdd(k, values));
		/// <summary>
		/// 返回两个给定位置之间的距离。如果两个位置之间的其中一个不存在， 那么命令返回空值。GEODIST 命令在计算距离时会假设地球为完美的球形， 在极限情况下， 这一假设最大会造成 0.5% 的误差。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member1">成员1</param>
		/// <param name="member2">成员2</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <returns>计算出的距离会以双精度浮点数的形式被返回。 如果给定的位置元素不存在， 那么命令返回空值。</returns>
		public double? GeoDist(string key, object member1, object member2, GeoUnit unit = GeoUnit.m) => ExecuteScalar(key, (c, k) => c.Value.GeoDist(k, member1, member2, unit));
		/// <summary>
		/// 返回一个或多个位置元素的 Geohash 表示。通常使用表示位置的元素使用不同的技术，使用Geohash位置52点整数编码。由于编码和解码过程中所使用的初始最小和最大坐标不同，编码的编码也不同于标准。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="members">多个查询的成员</param>
		/// <returns>一个数组， 数组的每个项都是一个 geohash 。 命令返回的 geohash 的位置与用户给定的位置元素的位置一一对应。</returns>
		public string[] GeoHash(string key, object[] members) => ExecuteScalar(key, (c, k) => c.Value.GeoHash(k, members));
		/// <summary>
		/// 从key里返回所有给定位置元素的位置（经度和纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="members">多个查询的成员</param>
		/// <returns>GEOPOS 命令返回一个数组， 数组中的每个项都由两个元素组成： 第一个元素为给定位置元素的经度， 而第二个元素则为给定位置元素的纬度。当给定的位置元素不存在时， 对应的数组项为空值。</returns>
		public (double longitude, double latitude)?[] GeoPos(string key, object[] members) => ExecuteScalar(key, (c, k) => c.Value.GeoPos(k, members));

		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public string[] GeoRadius(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadius(k, longitude, latitude, radius, unit, count, sorting, false, false, false)).Select(a => a.member).ToArray();
		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public T[] GeoRadius<T>(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytes(k, longitude, latitude, radius, unit, count, sorting, false, false, false)).Select(a => this.DeserializeRedisValueInternal<T>(a.member)).ToArray();

		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (string member, double dist)[] GeoRadiusWithDist(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadius(k, longitude, latitude, radius, unit, count, sorting, false, true, false)).Select(a => (a.member, a.dist)).ToArray();
		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (T member, double dist)[] GeoRadiusWithDist<T>(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytes(k, longitude, latitude, radius, unit, count, sorting, false, true, false)).Select(a => (this.DeserializeRedisValueInternal<T>(a.member), a.dist)).ToArray();

		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		private (string member, double longitude, double latitude)[] GeoRadiusWithCoord(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadius(k, longitude, latitude, radius, unit, count, sorting, true, false, false)).Select(a => (a.member, a.longitude, a.latitude)).ToArray();
		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		private (T member, double longitude, double latitude)[] GeoRadiusWithCoord<T>(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytes(k, longitude, latitude, radius, unit, count, sorting, true, false, false)).Select(a => (this.DeserializeRedisValueInternal<T>(a.member), a.longitude, a.latitude)).ToArray();

		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离、经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (string member, double dist, double longitude, double latitude)[] GeoRadiusWithDistAndCoord(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadius(k, longitude, latitude, radius, unit, count, sorting, true, true, false)).Select(a => (a.member, a.dist, a.longitude, a.latitude)).ToArray();
		/// <summary>
		/// 以给定的经纬度为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离、经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="longitude">经度</param>
		/// <param name="latitude">纬度</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (T member, double dist, double longitude, double latitude)[] GeoRadiusWithDistAndCoord<T>(string key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytes(k, longitude, latitude, radius, unit, count, sorting, true, true, false)).Select(a => (this.DeserializeRedisValueInternal<T>(a.member), a.dist, a.longitude, a.latitude)).ToArray();

		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public string[] GeoRadiusByMember(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusByMember(k, member, radius, unit, count, sorting, false, false, false)).Select(a => a.member).ToArray();
		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public T[] GeoRadiusByMember<T>(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			this.DeserializeRedisValueArrayInternal<T>(ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytesByMember(k, member, radius, unit, count, sorting, false, false, false)).Select(a => a.member).ToArray());

		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (string member, double dist)[] GeoRadiusByMemberWithDist(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusByMember(k, member, radius, unit, count, sorting, false, true, false)).Select(a => (a.member, a.dist)).ToArray();
		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (T member, double dist)[] GeoRadiusByMemberWithDist<T>(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytesByMember(k, member, radius, unit, count, sorting, false, true, false)).Select(a => (this.DeserializeRedisValueInternal<T>(a.member), a.dist)).ToArray();

		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		private (string member, double longitude, double latitude)[] GeoRadiusByMemberWithCoord(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusByMember(k, member, radius, unit, count, sorting, true, false, false)).Select(a => (a.member, a.longitude, a.latitude)).ToArray();
		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		private (T member, double longitude, double latitude)[] GeoRadiusByMemberWithCoord<T>(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytesByMember(k, member, radius, unit, count, sorting, true, false, false)).Select(a => (this.DeserializeRedisValueInternal<T>(a.member), a.longitude, a.latitude)).ToArray();

		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离、经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (string member, double dist, double longitude, double latitude)[] GeoRadiusByMemberWithDistAndCoord(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusByMember(k, member, radius, unit, count, sorting, true, true, false)).Select(a => (a.member, a.dist, a.longitude, a.latitude)).ToArray();
		/// <summary>
		/// 以给定的成员为中心， 返回键包含的位置元素当中， 与中心的距离不超过给定最大距离的所有位置元素（包含距离、经度、纬度）。
		/// </summary>
		/// <param name="key">不含prefix前辍</param>
		/// <param name="member">成员</param>
		/// <param name="radius">距离</param>
		/// <param name="unit">m 表示单位为米；km 表示单位为千米；mi 表示单位为英里；ft 表示单位为英尺；</param>
		/// <param name="count">虽然用户可以使用 COUNT 选项去获取前 N 个匹配元素， 但是因为命令在内部可能会需要对所有被匹配的元素进行处理， 所以在对一个非常大的区域进行搜索时， 即使只使用 COUNT 选项去获取少量元素， 命令的执行速度也可能会非常慢。 但是从另一方面来说， 使用 COUNT 选项去减少需要返回的元素数量， 对于减少带宽来说仍然是非常有用的。</param>
		/// <param name="sorting">排序</param>
		/// <returns></returns>
		public (T member, double dist, double longitude, double latitude)[] GeoRadiusByMemberWithDistAndCoord<T>(string key, object member, double radius, GeoUnit unit = GeoUnit.m, long? count = null, GeoOrderBy? sorting = null) =>
			ExecuteScalar(key, (c, k) => c.Value.GeoRadiusBytesByMember(k, member, radius, unit, count, sorting, true, true, false)).Select(a => (this.DeserializeRedisValueInternal<T>(a.member), a.dist, a.longitude, a.latitude)).ToArray();
		#endregion

		/// <summary>
		/// 开启分布式锁，若超时返回null
		/// </summary>
		/// <param name="name">锁名称</param>
		/// <param name="timeoutSeconds">超时（秒）</param>
		/// <returns></returns>
		public CSRedisClientLock Lock(string name, int timeoutSeconds) {
			name = $"CSRedisClientLock:{name}";
			var startTime = DateTime.Now;
			while (DateTime.Now.Subtract(startTime).TotalSeconds < timeoutSeconds) {
				if (this.Set(name, 1, timeoutSeconds, RedisExistence.Nx) == true) {
					return new CSRedisClientLock { Name = name, _client = this };
				}
				Thread.CurrentThread.Join(3);
			}
			return null;
		}
	}

	public class CSRedisClientLock : IDisposable {

		internal string Name { get; set; }
		internal CSRedisClient _client;

		/// <summary>
		/// 释放分布式锁
		/// </summary>
		public void Unlock() => _client.Del(this.Name);

		public void Dispose() {
			this.Unlock();
		}
	}

	public enum KeyType { None, String, List, Set, ZSet, Hash }
	public enum InfoSection { Server, Clients, Memory, Persistence, Stats, Replication, CPU, Keyspace }
	public enum ClientKillType { normal, slave, pubsub }
}