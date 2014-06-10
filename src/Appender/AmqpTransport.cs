using System;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using Apache.NMS.ActiveMQ;
using Apache.NMS;
using Apache.NMS.Util;


namespace Haukcode.AmqpJsonAppender 
{
    public class AmqpTransport
    {
        private static object locker = new object();
        private bool stopped;
        private ConnectionFactory factory;
        private IConnection connection;
        private ISession _session;
        private IDestination _destination;
        private IMessageProducer _producer;
        private ITextMessage _request;

        public string VirtualHost { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string Queue { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }

        public AmqpTransport()
        {           
        }
        public void Close()
        {
            stopped = true;
            lock (locker)
            {
                if (_session != null)
                    _session.Close();

                if (connection != null)
                    connection.Close();

                factory = null;
            }
        }

        private ConnectionFactory Factory
        {
            get
            {
                if (factory != null)
                    return factory;

                lock (locker)
                {
                    factory = new ConnectionFactory("activemq:tcp://WCT-WS016:61616");
                    return factory;
                }
            }
        }


        private IConnection Connection
        {
            get
            {
                if (connection != null)
                    return connection;

                lock (locker)
                {
                    factory = Factory;
                    connection = factory.CreateConnection(User, Password);
                    
                   
                    return connection;
                }
            }
        }

        private void Reset()
        {
            lock (locker)
            {
                if (_session != null)
                {
                    try
                    {
                        _session.Close();
                    }
                    catch 
                    {
                    }
                }
                _session = null;

                if (connection != null)
                {
                    try
                    {
                        connection.Close();
                    }
                    catch
                    {
                    }
                }
                connection = null;
                factory = null;
            }
        }

        public void Send(string message)
        {
            try
            {
                var connection = Connection;
                var session = connection.CreateSession();
                _request = session.CreateTextMessage(message);

                _destination = SessionUtil.GetDestination(session, "queue://elasticsearch");
                _producer = session.CreateProducer(_destination);

                if (!stopped)
                    _producer.Send(_request);
            }
            catch (Apache.NMS.ActiveMQ.BrokerException)
            {
                Reset();
                System.Threading.Thread.Sleep(5000);
            }
            
            catch (ThreadAbortException tae)
            {
                Console.WriteLine(tae);
            }
            catch (Exception e)
            {
                Reset();
            }
        }
    }
}
