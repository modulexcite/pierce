using System;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Pierce.Net
{
    public class Request
    {
        public Request()
        {
            ShouldCache = true;
        }

        public Uri Uri { get; set; }
        public int Sequence { get; set; }
        public bool ShouldCache { get; set; }
        public Action<Response> OnResponse { get; set; }

        public override string ToString()
        {
            return String.Format("{0}:{1}", Sequence, Uri);
        }
    }

    public class Response
    {
        public byte[] Data { get; set; }
        public Exception Exception { get; set; }
    }

    public class Network
    {
        public Response Execute(Request request)
        {
            var response = new Response();

            try
            {
                var client = new WebClient();
                response.Data = client.DownloadData(request.Uri);
            }
            catch (Exception ex)
            {
                response.Exception = ex;
            }

            return response;
        }
    }

    public class Cache
    {
        private readonly IDictionary<object, byte[]> _dictionary = new Dictionary<object, byte[]>();

        public bool TryGet(object key, out byte[] entry)
        {
            lock (_dictionary)
            {
                return _dictionary.TryGetValue(key, out entry);
            }
        }

        public void Put(object key, byte[] entry)
        {
            lock (_dictionary)
            {
                _dictionary[key] = entry;
            }
        }
    }

    public class RequestQueue
    {
        private readonly BlockingCollection<Request> _cache_queue = new BlockingCollection<Request>();
        private readonly BlockingCollection<Request> _network_queue = new BlockingCollection<Request>();
        private readonly IDictionary<Uri, List<Request>> _requests = new Dictionary<Uri, List<Request>>();

        private readonly Cache _cache;
        private readonly Network _network;

        private int sequence;

        public RequestQueue(Cache cache = null, Network network = null)
        {
            _cache = cache ?? new Cache();
            _network = network ?? new Network();

            Task.Factory.StartNew(CacheConsumer);

            Task.Factory.StartNew(NetworkConsumer);
            Task.Factory.StartNew(NetworkConsumer);
        }

        public Request Add(Request request)
        {
            request.Sequence = Interlocked.Increment(ref sequence);
            Console.WriteLine("Add(): {0}", request);

            if (!request.ShouldCache)
            {
                _network_queue.Add(request);
                return request;
            }

            var key = request.Uri;
            lock (_requests)
            {
                List<Request> list;
                if (_requests.TryGetValue(key, out list))
                {
                    list = list ?? new List<Request>();
                    list.Add(request);

                    _requests[key] = list;
                }
                else
                {
                    _requests[key] = null;
                    _cache_queue.Add(request);
                }                
            }

            return request;
        }

        private void CacheConsumer()
        {
            foreach (var request in _cache_queue.GetConsumingEnumerable())
            {
                byte[] data;
                if (_cache.TryGet(request.Uri, out data))
                {
                    var response = new Response { Data = data };
                    Complete(request, response);
                }
                else
                {
                    _network_queue.Add(request);
                }
            }
        }

        private void NetworkConsumer()
        {
            foreach (var request in _network_queue.GetConsumingEnumerable())
            {
                Console.WriteLine("NetworkConsumer: {0}", request);

                var response = _network.Execute(request);

                if (request.ShouldCache)
                {
                    _cache.Put(request.Uri, response.Data);
                }

                Complete(request, response);
            }
        }

        private void Complete(Request request, Response response)
        {
            if (request.ShouldCache)
            {
                var key = request.Uri;
                List<Request> list;

                lock (_requests)
                {
                    if (_requests.TryGetValue(key, out list) &&
                        _requests.Remove(key) &&
                        list != null)
                    {
                        list.ForEach(_cache_queue.Add);
                    }
                }
            }

            request.OnResponse(response);
        }
    }
}
