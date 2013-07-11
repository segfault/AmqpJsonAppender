using System;
using System.Collections.Generic;
using System.Threading;
using System.Text;


namespace Haukcode.AmqpJsonAppender
{
    public class LossyBlockingQueue<T> : IDisposable where T : class
    {
        private Queue<T> _queue = new Queue<T>();
        private Semaphore _semaphore = new Semaphore(0, int.MaxValue);
        private int maxQueueLength;

        public LossyBlockingQueue(int maxQueueLength)
        {
            this.maxQueueLength = maxQueueLength;
        }

        public void Enqueue(T data)
        {
            if (data == null) throw new ArgumentNullException("data");
            bool added = false;
            lock (_queue)
            {
                if (_queue.Count < maxQueueLength)
                {
                    // Only queue if we have less than X items in the queue
                    _queue.Enqueue(data);
                    added = (_queue.Count % 5) == 0;
                }
            }
            if(added)
                _semaphore.Release();
        }

        public T Dequeue()
        {
            _semaphore.WaitOne(3000);

            lock (_queue)
            {
                if (_queue.Count == 0)
                    return null;

                return _queue.Dequeue();
            }
        }

        public void Dispose()
        {
            if (_semaphore != null)
            {
                _semaphore.Close();
                _semaphore = null;
            }
        }
    }
}