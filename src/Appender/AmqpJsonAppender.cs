﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Collections;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading;
using System.Xml;
using System.IO;

using log4net.Appender;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Haukcode.AmqpJsonAppender
{
    public class AmqpJsonAppender : AppenderSkeleton, IDisposable
    {
        public static string UNKNOWN_HOST = "unknown_host";

        private AmqpTransport amqpTransport;
        private LossyBlockingQueue<string> loggingBuffer;

        private static readonly object locker = new object();
        private static long sequence = 0;
        private string additionalFields;
        private Thread messagePump;
        private string version;
        private bool active;


        //---------------------------------------
        //configuration settings for the appender
        private Dictionary<string, string> innerAdditionalFields;

        public string AdditionalFields
        {
            get
            {
                return additionalFields;
            }
            set
            {
                additionalFields = value;

                if (additionalFields != null)
                {
                    innerAdditionalFields = new Dictionary<string, string>();
                }
                else
                {
                    innerAdditionalFields.Clear();
                }
                var fields = additionalFields.Split(',');
                var dict = new Dictionary<string, string>();
                foreach (var field in fields)
                {
                    string[] kvp = field.Split(':');
                    if (kvp.Length == 2)
                        dict.Add(kvp[0], kvp[1]);
                }
                innerAdditionalFields = dict;
            }
        }

        public string Facility { get; set; }
        public string AmqpServerHost { get; set; }
        public int AmqpServerPort { get; set; }
        public string Host { get; set; }
        public bool IncludeLocationInformation { get; set; }

        public string AmqpUser { get; set; }
        public string AmqpPassword { get; set; }
        public string AmqpVirtualHost { get; set; }
        public string AmqpQueue { get; set; }
        public bool StripEmptyMessages { get; set; }
        public int BufferSize { get; set; }
        public string IndexId { get; set; }


        public AmqpJsonAppender()
            : base()
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly();
            if (assembly != null)
                Facility = assembly.GetName().Name;
            assembly = System.Reflection.Assembly.GetAssembly(typeof(AmqpJsonAppender));
            if (assembly != null)
                version = assembly.GetName().Version.ToString();
            AmqpServerHost = "";
            AmqpServerPort = 5672;
            Host = null;
            IncludeLocationInformation = false;
            StripEmptyMessages = true;

            AmqpUser = "guest";
            AmqpPassword = "guest";
            AmqpVirtualHost = "/";
            AmqpQueue = "elasticsearch";
            IndexId = "logs-{0:yyyy}.{1}";

            BufferSize = 500;

            active = true;
            messagePump = new Thread(ThreadProc);
            messagePump.IsBackground = true;
        }

        protected override void OnClose()
        {
            active = false;

            if (amqpTransport != null)
            {
                amqpTransport.Close();
                amqpTransport = null;
            }

            if (loggingBuffer != null)
            {
                loggingBuffer.Dispose();
            }

            if (messagePump != null && messagePump.IsAlive)
            {
                messagePump.Abort();
                messagePump.Join(5000);
            }

            messagePump = null;
            loggingBuffer = null;

            base.OnClose();
        }


        private LossyBlockingQueue<string> LoggingBuffer
        {
            get
            {
                if (loggingBuffer != null)
                    return loggingBuffer;

                lock (locker)
                {
                    loggingBuffer = new LossyBlockingQueue<string>(BufferSize);
                }

                messagePump.Start(loggingBuffer);

                return loggingBuffer;
            }
        }


        private long GetSequenceNumber()
        {
            lock (locker)
                return ++sequence;
        }


        private void ThreadProc(object stateInfo)
        {
            try
            {
                var queue = (LossyBlockingQueue<string>)stateInfo;

                while (active)
                {
                    try
                    {
                        var jsonMessage = queue.Dequeue();
                        if (jsonMessage == null)
                            continue;

                        SendAmqpMessage(jsonMessage);
                    }
                    catch (InvalidOperationException)
                    {
                        // Ignore
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine(ex.ToString());
#endif
            }
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="loggingEvent"></param>
        protected override void Append(log4net.Core.LoggingEvent loggingEvent)
        {
            string jsonString = CreateJsonFromLoggingEvent(loggingEvent, GetSequenceNumber());
            if (jsonString != null)
                LoggingBuffer.Enqueue(jsonString);
        }

        /// <summary>
        /// Get the HostName
        /// </summary>
        private string LoggingHostName
        {
            get
            {
                string ret = Host;
                if (ret == null)
                {
                    try
                    {
                        return System.Net.Dns.GetHostName();
                    }
                    catch
                    {
                        return UNKNOWN_HOST;
                    }
                }
                return ret;
            }
        }


        /// <summary>
        /// Sending the message via AMQP
        /// </summary>
        /// <param name="message">Message to be sent</param>
        private void SendAmqpMessage(string message)
        {
            if (amqpTransport == null)
            {
                amqpTransport = new AmqpTransport()
                {
                    IpAddress = GetIpAddressFromHostName(),
                    Port = AmqpServerPort,
                    VirtualHost = AmqpVirtualHost,
                    User = AmqpUser,
                    Password = AmqpPassword,
                    Queue = AmqpQueue
                };
            }

            amqpTransport.Send(message);

        }

        /// <summary>
        /// Get the first IPAddress from the HostName
        /// </summary>
        /// <returns>IPAddress as string</returns>
        private string GetIpAddressFromHostName()
        {
            IPAddress ipAddress;
            if (!IPAddress.TryParse(AmqpServerHost, out ipAddress))
            {
                var addresslist = Dns.GetHostAddresses(AmqpServerHost);

                foreach (var address in addresslist)
                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return address.ToString();

                throw new ArgumentException("No IPv4 address found");
            }

            return ipAddress.ToString();
        }


        private string CreateJsonFromLoggingEvent(log4net.Core.LoggingEvent loggingEvent, long sequence)
        {
            Dictionary<string, string> additionalFields;
            if (innerAdditionalFields != null)
                additionalFields = new Dictionary<string, string>(innerAdditionalFields);
            else
                additionalFields = new Dictionary<string, string>();

            var message = RenderLoggingEvent(loggingEvent);
            string fullMessage = message;

            int messagePos = message.IndexOf("Message:");
            if (messagePos > -1)
            {
                // Special format
                string otherProperties = message.Substring(0, messagePos);
                fullMessage = message.Substring(messagePos + 8);

                var extraFields = new Dictionary<string, string>();
                foreach (var part in otherProperties.Split(','))
                {
                    if (string.IsNullOrEmpty(part))
                        continue;
                    var keyValue = part.Split(':');
                    if (keyValue.Length > 1)
                        extraFields.Add(keyValue[0], keyValue[1]);
                }

                foreach (var extraField in extraFields)
                    additionalFields.Add(extraField.Key, extraField.Value);
            }


            if (loggingEvent.ExceptionObject != null)
            {
                fullMessage = String.Format("{0} - {1}. {2}. {3}.", fullMessage, loggingEvent.ExceptionObject.Source, loggingEvent.ExceptionObject.Message, loggingEvent.ExceptionObject.StackTrace);
            }

            if (StripEmptyMessages && string.IsNullOrEmpty(fullMessage))
                return null;

            var jsonMessage = new JsonMessage
            {
                Facility = (this.Facility ?? "LOG4NET"),
                File = "",
                FullMessage = fullMessage,
                Host = LoggingHostName,
                Level = loggingEvent.Level.ToString(),
                Line = "",
                Timestamp = loggingEvent.TimeStamp,
                Sequence = sequence
            };

            var dateInfo = System.Globalization.DateTimeFormatInfo.CurrentInfo;
            jsonMessage.Index = string.Format(IndexId, jsonMessage.Timestamp,
                dateInfo.Calendar.GetWeekOfYear(jsonMessage.Timestamp, dateInfo.CalendarWeekRule, dateInfo.FirstDayOfWeek));

            //only set location information if configured
            if (IncludeLocationInformation)
            {
                jsonMessage.File = loggingEvent.LocationInformation.FileName;
                jsonMessage.Line = loggingEvent.LocationInformation.LineNumber;
            }

            //add additional fields and prepend with _ if not present already
            if (additionalFields != null)
            {
                foreach (var item in additionalFields)
                {
                    AddAdditionalFields(item.Key, item.Value, jsonMessage);
                }
            }
            AddAdditionalFields("appender-version", version, jsonMessage);

            //add additional fields and prepend with _ if not present already
            if (loggingEvent.Properties != null)
            {
                foreach (DictionaryEntry item in loggingEvent.Properties)
                {
                    var key = item.Key as string;
                    if (key != null)
                    {
                        AddAdditionalFields(key, item.Value as string, jsonMessage);
                    }
                }
            }

            return jsonMessage.GetJSON();
        }

        /// <summary>
        /// Add    
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="json"></param>
        private void AddAdditionalFields(string key, string value, JsonMessage message)
        {
            if (key != null)
            {
                key = Regex.Replace(key, "[\\W]", "");
                message.AdditionalFields.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        public void Dispose()
        {
            OnClose();
        }
    }

    public class JsonMessage
    {
        public string Facility { get; set; }

        public string File { get; set; }

        public string FullMessage { get; set; }

        public string Host { get; set; }

        public string Level { get; set; }

        public string Line { get; set; }

        public string Index { get; set; }

        public DateTime Timestamp { get; set; }

        public long Sequence { get; set; }

        public List<KeyValuePair<string, string>> AdditionalFields { get; private set; }

        public string TimestampISO8601
        {
            get
            {
                return XmlConvert.ToString(Timestamp, XmlDateTimeSerializationMode.RoundtripKind);
            }
        }

        public JsonMessage()
        {
            this.AdditionalFields = new List<KeyValuePair<string, string>>();
        }

        private void addObjKvp(JsonTextWriter jsw, string key, string val)
        {
            jsw.WritePropertyName(key);
            jsw.WriteValue(val);
        }

        public string GetJSON()
        {
            var ms = new MemoryStream();
            var sw = new StreamWriter(ms);
            var jsw = new JsonTextWriter(sw);



            jsw.WriteStartObject();
            addObjKvp(jsw, "@source", "amqpjsonappender");
            addObjKvp(jsw, "@source_path", "amqpjsonappender");
            addObjKvp(jsw, "@source_host", Host);
            addObjKvp(jsw, "@type", "log4net");
            addObjKvp(jsw, "@timestamp", TimestampISO8601);
            addObjKvp(jsw, "@message", FullMessage);
            addObjKvp(jsw, "@seq", Sequence.ToString());

            jsw.WritePropertyName("@fields");
            jsw.WriteStartObject();
            addObjKvp(jsw, "facility", Facility);
            addObjKvp(jsw, "file", File);
            addObjKvp(jsw, "level", Level);
            addObjKvp(jsw, "line", Line);

            foreach (var kvp in AdditionalFields)
                addObjKvp(jsw, kvp.Key, kvp.Value);

            jsw.WriteEndObject();
            jsw.WriteEndObject();
            jsw.Flush();

            var output = UTF8Encoding.UTF8.GetString(ms.ToArray());

            return output;
        }
    }
}
