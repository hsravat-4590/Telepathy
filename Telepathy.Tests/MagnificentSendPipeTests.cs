using System;
using NUnit.Framework;

namespace Telepathy.Tests
{
    public class MagnificentSendPipeTests
    {
        const int MaxMessageSize = 64;
        MagnificentSendPipe pipe;

        [SetUp]
        public void SetUp()
        {
            pipe = new MagnificentSendPipe(MaxMessageSize);
        }

        [Test]
        public void Enqueue()
        {
            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0x1}));
            Assert.That(pipe.Count, Is.EqualTo(1));

            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0x2}));
            Assert.That(pipe.Count, Is.EqualTo(2));
        }

        [Test]
        public void DequeueAndSerializeAll()
        {
            // enqueue two
            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0xAA}));
            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0xBB, 0xCC}));

            // pass an empty payload. DequeueAll should initialize / scale it!
            byte[] payload = null;

            // dequeue and serialize all
            bool result = pipe.DequeueAndSerializeAll(ref payload, out int packetSize);
            Assert.That(result, Is.True);
            // header + content, header + content
            Assert.That(packetSize, Is.EqualTo(4+1 + 4+2));
            // first header
            Assert.That(payload[0], Is.EqualTo(0x00));
            Assert.That(payload[1], Is.EqualTo(0x00));
            Assert.That(payload[2], Is.EqualTo(0x00));
            Assert.That(payload[3], Is.EqualTo(0x01));
            // first content
            Assert.That(payload[4], Is.EqualTo(0xAA));
            // second header
            Assert.That(payload[5], Is.EqualTo(0x00));
            Assert.That(payload[6], Is.EqualTo(0x00));
            Assert.That(payload[7], Is.EqualTo(0x00));
            Assert.That(payload[8], Is.EqualTo(0x02));
            // second content
            Assert.That(payload[9], Is.EqualTo(0xBB));
            Assert.That(payload[10], Is.EqualTo(0xCC));

            // pipe should be empty now
            Assert.That(pipe.Count, Is.EqualTo(0));
        }

        [Test]
        public void Clear()
        {
            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0x1}));
            Assert.That(pipe.Count, Is.EqualTo(1));

            pipe.Clear();
            Assert.That(pipe.Count, Is.EqualTo(0));
        }

        // make sure pooling works as intended
        [Test]
        public void Pooling()
        {
            // pool should be empty first
            Assert.That(pipe.PoolCount, Is.EqualTo(0));

            // enqueue one. pool is empty so it should allocate a new byte[]
            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0x1}));
            Assert.That(pipe.PoolCount, Is.EqualTo(0));

            // dequeue all. should return the byte[] to the pool
            byte[] payload = null;
            pipe.DequeueAndSerializeAll(ref payload, out int _);
            Assert.That(pipe.PoolCount, Is.EqualTo(1));

            // enqueue one. should use the pooled entry
            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0x2}));
            Assert.That(pipe.PoolCount, Is.EqualTo(0));

            // enqueue another one. pool is empty so it should allocate a new byte[]
            pipe.Enqueue(new ArraySegment<byte>(new byte[]{0x3}));
            Assert.That(pipe.PoolCount, Is.EqualTo(0));

            // clear. should return both to pool.
            pipe.Clear();
            Assert.That(pipe.PoolCount, Is.EqualTo(2));
        }
    }
}