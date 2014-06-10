using System;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using Apache.NMS.ActiveMQ;
using Apache.NMS;
using Apache.NMS.Util;
//using RabbitMQ.Client;

namespace Haukcode.AmqpJsonAppender 
{
    public class AmqpTransport
    {
        private static object locker = new object();
        private bool _stopped;
        private ConnectionFactory _factory;
        private IConnection _connection;
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
            _stopped = true;
            lock (locker)
            {
                if (_session != null)
                    _session.Close();

                if (_connection != null)
                    _connection.Close();

                _factory = null;
            }
        }

        private ConnectionFactory Factory
        {
            get
            {
                if (_factory != null)
                    return _factory;

                lock (locker)
                {
                    _factory = new ConnectionFactory("activemq:tcp://WCT-WS016:61616");
                    return _factory;
                }
            }
        }


        private IConnection Connection
        {
            get
            {
                if (_connection != null)
                    return _connection;

                lock (locker)
                {
                    _factory = Factory;
                    _connection = _factory.CreateConnection(User, Password);
                    
                   
                    return _connection;
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

                if (_connection != null)
                {
                    try
                    {
                        _connection.Close();
                    }
                    catch
                    {
                    }
                }
                _connection = null;
                _factory = null;
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

                if (!_stopped)
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
                throw e;
                Reset();
            }
        }
    }
}
