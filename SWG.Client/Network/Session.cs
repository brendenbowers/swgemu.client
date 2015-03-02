﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Collections.Concurrent;

using SWG.Client.Utils;



namespace SWG.Client.Network
{
    public class Session
    {

        private static NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        public UInt32 EncryptionKey { get; set; }
        public UInt32 WindowResendSize { get; set; }
        public UInt32 WindowSizeCurrent { get; set; }
        public UInt32 MaxPacketSize { get; set; }
        public UInt32 MaxUreliablePacketSize { get; set; }
        public SessionStatus Status { get; set; }

        private Int32 _requestID;
        private UInt32 _pingSequence;
        private UInt32 _fragmentedPacketTotalSize;
        private UInt32 _fragmentedPacketCurrentSize;
        private UInt32 _fragmentedPacketStartSequence;
        private UInt32 _fragmentedPacketCurrentSequence;

        private UInt32 _routedFragmentedPacketTotalSize;
        private UInt32 _routedFragmentedPacketCurrentSize;
        private UInt32 _routedFragmentedPacketStartSequence;
        private UInt32 _routedFragmentedPacketCurrentSequence;

        private UInt32 _serverTickCount;
        private UInt32 _lastRoundtripTime;
        private UInt32 _averageRoundtripTime;
        private UInt32 _shortestRoundtripTime;
        private UInt32 _longestRoundtripTime;
        private UInt32 _clientPacketsSent;
        private UInt32 _clientPacketsReceived;
        private UInt32 _serverPacketsSent;
        private UInt32 _serverPacketsReceived;
        private UInt64 _lastPingPacketRecieved;

        // Reliability
        public UInt16 _outSequenceNext =0;
        public UInt16 _inSequenceNext = 0;

        private UInt32 _nextPacketSequenceSent;
        private UInt64 _lastRemotePacketAckReceived;
        private volatile UInt32 _windowSizeCurrent;
        private UInt32 _windowResendSize;

        private UInt32 _lastSequenceAcked;

        private Queue<Message> _outgoingMessageQueue = new Queue<Message>();
        private ConcurrentQueue<Message> _unreliableMessageQueue = new ConcurrentQueue<Message>();
        public Queue<Message> IncomingMessageQueue { get; set; }

        public int RequestId
        {
            get { return _requestID; }
            set { _requestID = value; }
        }

        private Queue<Message> _multiMessageQueue = new Queue<Message>();
        private Queue<Message> _routedMultiMessageQueue = new Queue<Message>();
        private Queue<Message> _unreliableMultiMessageQueue = new Queue<Message>();

        private ConcurrentQueue<Packet> _reliableOutgoingPacketQueue = new ConcurrentQueue<Packet>();
        private ConcurrentQueue<Packet> _unreliableOutgoingPacketQueue = new ConcurrentQueue<Packet>();

        private List<Packet> _windowPacketList = new List<Packet>();
        private List<Packet> _rolloverWindowPacketList = new List<Packet>();
        private List<Packet> _newRolloverWindowPacketList = new List<Packet>();
        public  List<Packet> _newWindowPacketList = new List<Packet>();

        private Queue<Packet> _incomingFragmentedPacketQueue = new Queue<Packet>();
        private Queue<Packet> _incomingRoutedFragmentedPacketQueue = new Queue<Packet>();

        private List<Packet> _incomingPacketList = new List<Packet>();

        private UInt64 _lastPacketRecieved;
        private UInt64 _lastPacketSent;

        private UInt64 _lastWriteThreadTime = 0;
        private UInt64 _lastWriteTimeoutCheck = 0;
        private UInt64 _connectStart = 0;
        private UInt64 _lastConnectAttempt = 0;

        private bool _outOfOrder = false;
        private bool _sendDelayedAcks = false;
        private bool _outSequenceRollover = false;

        public SessionCommand Command = SessionCommand.None;


        public Session()
        {
            MaxPacketSize = 496;
            MaxUreliablePacketSize = 496;
            _windowSizeCurrent = 8000;
            WindowResendSize = 8000;
            IncomingMessageQueue = new Queue<Message>();
        }


        public void ProcessWriteThread()
        {
            UInt64 now = DateTime.UtcNow.GetMilliseconds();

            if (_unreliableMessageQueue.Count > 0 && _outgoingMessageQueue.Count > 0 && _newWindowPacketList.Count > 0)
            {
                if (!_sendDelayedAcks && now - _lastWriteThreadTime < 500)
                {
                    return;
                }
            }

            _lastWriteThreadTime = now;

            if (_sendDelayedAcks)
            {
                _sendDelayedAcks = false;

                Packet packet = new Packet();
                packet.AddData(Convert.ToUInt16(SessionOp.DataAck1));
                packet.AddNetworkData((UInt16)(_inSequenceNext - 1));
                packet.Size = packet.WriteIndex;
                packet.Compressed = false;
                packet.Encrypted = true;

                _AddOutgoingUnreliablePacket(packet);
            }

            UInt32 packetsBuilt = 0;
            UInt32 unreliablePacketsBuilt = 0;

            while (packetsBuilt < 200 && _outgoingMessageQueue.Count > 0)
            {
                packetsBuilt += _BuildPackets();
            }

            while (unreliablePacketsBuilt < 200 && _unreliableMessageQueue.Count > 0)
            {
                unreliablePacketsBuilt += _BuildUreliablePackets();
            }

            UInt32 packetsSent = 0;

            if (_outSequenceRollover)
            {
                var newRolloverWindowPacketList = new List<Packet>(_newRolloverWindowPacketList);
                foreach (Packet packet in newRolloverWindowPacketList)
                {
                    packet.ReadIndex = 2;
                    if (packetsSent >= _windowSizeCurrent)
                    {
                        break;
                    }

                    _AddOutgoingReliablePacket(packet);
                    packet.TimeQueued = DateTimeExt.GetStoredMilliseconds();

                    _newRolloverWindowPacketList.Remove(packet);
                    _rolloverWindowPacketList.Add(packet);

                    packetsSent++;
                    _nextPacketSequenceSent++;
                }
            }

            var newWindowPacketList = new List<Packet>(_newWindowPacketList);
            foreach (Packet packet in newWindowPacketList)
            {
                if(packetsSent >= _windowSizeCurrent)
                    break;

                _AddOutgoingReliablePacket(packet);

                _windowPacketList.Add(packet);

                packetsSent++;
                ++_nextPacketSequenceSent;

                _newWindowPacketList.Remove(packet);
            }

            switch (Command)
            {
                case SessionCommand.Connect:
                    _ProcessConnectCommand();
                    break;
                case SessionCommand.Disconnect:
                    _ProcessDisconectCommand();
                    break;
            }

            

            if (now - _lastWriteTimeoutCheck < 1000)
            {
                return;
            }


            if (Status == SessionStatus.Connected)
            {
                UInt64 diff = now - _lastPacketRecieved;
                

                if (diff > 60000)
                {
                    Command = SessionCommand.Disconnect;
                }
                else if (diff > 10000)
                {
                    _SendPingPacket();
                }

                _ResendData(); 
            }

        }


        private void _SendPingPacket()
        {

            /*Console.WriteLine("sending ping");
            Packet packet = new Packet();
            packet.AddData((UInt16)SessionOp.Ping);

            packet.Compressed = false;
            packet.Encrypted = true;

            _AddOutgoingUnreliablePacket(packet, true);*/
        }


        public void SendChannelA(Message message)
        {
            if (Status != SessionStatus.Connected)
            {
                return;
            }

            if (message.FastPath /*|| message.Size < MaxUreliablePacketSize*/)
            {
                _unreliableMessageQueue.Enqueue(message);
            }
            else
            {
                message.FastPath = false;
                _outgoingMessageQueue.Enqueue(message);
            }
        }


        public void SendChannelAUnreliable(Message message)
        {
            if (Status != SessionStatus.Connected)
            {
                return;
            }

            if (message.Size < MaxUreliablePacketSize)
            {
                _unreliableMessageQueue.Enqueue(message);
            }
            else
            {
                _outgoingMessageQueue.Enqueue(message);
            }
        }


        public void HandleSessionPacket(Packet packet)
        {
            packet.ReadIndex = 0;

            _lastPacketRecieved = DateTimeExt.GetStoredMilliseconds();

            if (packet.PacketType < 0x0100)
            {
                HandleFastpahPacket(packet);
                return;
            }


            var packetType = packet.PacetTypeEnum;

            _logger.Debug("HandleSessionPacket: type: {0}. len: {1}", packetType, packet.Size);

            switch (packetType)
            {
                case SessionOp.SessionResponse:
                    _ProcessSessionResponsePacket(packet);
                    return;
                case SessionOp.DataOrder1:
                case SessionOp.DataOrder2:
                case SessionOp.DataOrder3:
                case SessionOp.DataOrder4:
                    _ProcessDataOrderPacket(packet);
                    return;
                case SessionOp.MultiPacket:
                    _ProcessMultiPacket(packet);
                    return;
                case SessionOp.Disconnect:
                    Status = SessionStatus.Disconnecting;
                    _ProcessDisconnectPacket(packet);
                    return;
                case SessionOp.DataAck1:
                case SessionOp.DataAck2:
                case SessionOp.DataAck3:
                case SessionOp.DataAck4:
                    _ProcessDataChannelAck(packet);
                    return;
                case SessionOp.Ping:
                    _ProcessPingPacket(packet);
                    return;
                case SessionOp.NetStatResponse:
                    _ProcessNetStatResponsePacket(packet);
                    return;
            }

            packet.ReadIndex = 2;
            UInt16 sequence = packet.ReadNetworkUInt16();
            if (_inSequenceNext == sequence)
            {
                SortSessionPacket(packet, packetType);
                _outOfOrder = false;
            }
            else if (_inSequenceNext < sequence)
            {

                _logger.Warn("out of order packet. Recieved Seq: {0}. Expected: {1}", sequence, _inSequenceNext);
                var prevPacket = _inSequenceNext - 1;
                if (prevPacket < 0)
                {
                    prevPacket = 0;
                }
                Packet orderPacket = new Packet();
                orderPacket.AddData(Convert.ToUInt16(SessionOp.DataOrder1));
                orderPacket.AddNetworkData(prevPacket);
                orderPacket.Compressed = false;
                orderPacket.Encrypted = true;

                _AddOutgoingUnreliablePacket(orderPacket, true);
            }
            else
            {
                _logger.Debug("Sequence {0} lesser than next seq {1}. Dropping.", sequence, _inSequenceNext);
            }
        }


        public void SortSessionPacket(Packet packet, SessionOp packetType)
        {
            packet.ReadIndex = 2;

            switch (packetType)
            {
                case SessionOp.DataChannel2:
                    _ProcessDataChannelB(packet);
                    break;
                case SessionOp.DataChannel1:
                case SessionOp.DataChannel3:
                case SessionOp.DataChannel4:
                    _ProcessDataChannelPacket(packet, false);
                    break;
                case SessionOp.DataFrag2:
                    _ProcessRoutedFragmentedPacket(packet);
                    break;
                case SessionOp.DataFrag1:
                case SessionOp.DataFrag3:
                case SessionOp.DataFrag4:
                    _ProcessFragmentedPacket(packet);
                    break;
            }
        }


        public void HandleFastpahPacket(Packet packet)
        {
            _logger.Debug("HandleFastpahPacket: type: {0}. len: {1}", packet.PacetTypeEnum, packet.Size);

            byte priority = 0;
            byte routed = 0;
            byte dest = 0;
            UInt32 accountId = 0;
            int offset = 2;

            _lastPacketRecieved = DateTimeExt.GetStoredMilliseconds();

            packet.ReadIndex = 0;

            priority = packet.ReadByte();
            routed = packet.ReadByte();
            if (routed != 0)
            {
                dest = packet.ReadByte();
                accountId = packet.ReadUInt32();
                offset = 7;
            }
            
            var msg = new Message(packet.Data, offset, packet.Size - offset, packet.Size -2)
                {
                        Priority = priority,
                        FastPath = true,
                };

            _AddIncomingMessage(msg, priority);

        }


        public Packet GetOutgoingReliablePacket()
        {
            Packet packet = null;
            if (_reliableOutgoingPacketQueue.TryDequeue(out packet))
            {
                _serverPacketsSent++;
                _lastPacketSent = DateTimeExt.GetStoredMilliseconds();
            }

            return packet;
        }


        public Packet GetOutgoingUnreliablePacket()
        {
            Packet packet = null;
            if (_unreliableOutgoingPacketQueue.TryDequeue(out packet))
            {
                _serverPacketsSent++;
                _lastPacketSent = DateTimeExt.GetStoredMilliseconds();
            }

            return packet;
        }



        protected void _AddIncomingMessage(Message message, byte priority = 0)
        {
            IncomingMessageQueue.Enqueue(message);
        }


        protected void _AddOutgoingReliablePacket(Packet packet, bool SetSizeFromWriteIndex = false)
        {
            if (SetSizeFromWriteIndex)
            {
                packet.Size = packet.WriteIndex;
            }

            packet.TimeQueued = DateTimeExt.GetStoredMilliseconds();

            _reliableOutgoingPacketQueue.Enqueue(packet);
        }


        protected void _AddOutgoingUnreliablePacket(Packet packet, bool SetSizeFromWriteIndex = false)
        {
            if (SetSizeFromWriteIndex)
            {
                packet.Size = packet.WriteIndex;
            }

            packet.TimeQueued = DateTimeExt.GetStoredMilliseconds();

            _unreliableOutgoingPacketQueue.Enqueue(packet);
        }


        protected void _ProcessSessionResponsePacket(Packet packet)
        {
            Console.WriteLine(BitConverter.ToString(packet.Data,0,packet.Size));

            packet.ReadIndex = 2;
            var requestID = packet.ReadUInt32();
            if (requestID != RequestId)
            {
                _logger.Debug("Warning. request id does not match. Recieved: {0}. Expected {1}", requestID, RequestId)  ;
            }

            EncryptionKey = packet.ReadNetworkUInt32();
            
            var crcLength = packet.ReadByte();
            var useCompression = packet.ReadByte();
            var seedSise = packet.ReadByte();
            var udpPacketSize = packet.ReadNetworkUInt32();

            if (udpPacketSize != 0)
            {
                MaxPacketSize = udpPacketSize;
            }

            _logger.Debug("CRC Len: {0}. Compression: {1}. Seed Size: {2}. UDP Packet Size: {3}", crcLength, useCompression, seedSise, udpPacketSize);

            Status = SessionStatus.Connected;
            if (Command == SessionCommand.Connect)
            {
                Command = SessionCommand.None;
            }
        }


        protected void _ProcessDataOrderPacket(Packet packet)
        {
            packet.ReadIndex = 2;

            UInt32 hbsequence = packet.PeekUInt32();
            UInt16 sequence = packet.ReadNetworkUInt16();
            _logger.Debug("Out of order sequence: {0}. hb order: {1}", sequence, hbsequence);
            Packet windowPacket = _windowPacketList.FirstOrDefault();
            if (windowPacket == null)
                return;

            windowPacket.ReadIndex = 2;
            UInt16 windowSeq = windowPacket.ReadNetworkUInt16();

            if (sequence < windowSeq)
            {
                return;
            }

            if (sequence > windowSeq + _windowPacketList.Count)
            {
                return;
            }

            if (WindowSizeCurrent > (WindowResendSize / 10))
            {
                WindowSizeCurrent = 0;
            }

            if (_rolloverWindowPacketList.Count != 0 && (sequence > (65535 - _newRolloverWindowPacketList.Count)))
            {
                foreach (Packet rollPacket in _rolloverWindowPacketList)
                {
                    rollPacket.ReadIndex = 2;
                    UInt16 rollSequence = rollPacket.ReadNetworkUInt16();

                    if (rollSequence < sequence)
                    {
                        if (rollPacket.OOHTimeSent - DateTimeExt.GetStoredMilliseconds() < 200)
                        {
                            break;
                        }

                        _AddOutgoingReliablePacket(rollPacket);
                        rollPacket.OOHTimeSent = DateTimeExt.GetStoredMilliseconds();

                        if (WindowSizeCurrent > (WindowResendSize / 10))
                        {
                            WindowSizeCurrent--;
                        }
                    }
                }
            }


            UInt64 localTime = DateTimeExt.GetStoredMilliseconds();
            UInt16 seq;

            foreach (Packet winPacket in _windowPacketList)
            {
                winPacket.ReadIndex = 2;
                seq = winPacket.ReadNetworkUInt16();

                UInt64 old = windowPacket.OOHTimeSent;

                if (localTime - old > 10000)
                {
                    _AddOutgoingUnreliablePacket(winPacket);
                    winPacket.OOHTimeSent = localTime;
                }
                else
                {
                    return;
                }
            }



        }


        protected void _ProcessMultiPacket(Packet packet)
        {
            while (packet.ReadIndex < packet.Size)
            {
                var subPacketSize = Convert.ToInt32(packet.ReadByte());
                var subPacket = new Packet(packet.Data, packet.ReadIndex, subPacketSize);
                packet.ReadIndex += subPacketSize;

                HandleSessionPacket(subPacket);
            }
        }


        protected void _ProcessDisconnectPacket(Packet packet)
        {
            Status = SessionStatus.Disconnected;
        }


        protected void _ProcessDataChannelAck(Packet packet)
        {

            packet.ReadIndex = 2;
            UInt16 windowSeq = 0;
            UInt16 sequence = packet.ReadNetworkUInt16();
            Packet windowPacket = null;

            Int32 acked = 0;

            if (_outSequenceRollover)
            {
                if (_rolloverWindowPacketList.Count == 0 || _newRolloverWindowPacketList.Count == 0)
                {
                    _outSequenceRollover = false;
                }
                else if (sequence < (0xFFFF - (_rolloverWindowPacketList.Count + _newRolloverWindowPacketList.Count)))
                {
                    _lastRemotePacketAckReceived = DateTimeExt.GetStoredMilliseconds();

                    if (WindowSizeCurrent < WindowResendSize)
                    {
                        WindowSizeCurrent += WindowResendSize / 10;

                        if (WindowSizeCurrent > WindowResendSize)
                        {
                            WindowSizeCurrent = WindowResendSize;
                        }
                    }

                    acked += _rolloverWindowPacketList.Count;
                    _rolloverWindowPacketList.Clear();
                }
                else
                {
                    windowPacket = _rolloverWindowPacketList.FirstOrDefault();
                    windowSeq = windowPacket.ReadNetworkUInt16();

                    if (sequence > windowSeq)
                    {
                        if (WindowSizeCurrent < WindowResendSize)
                        {
                            WindowSizeCurrent += WindowResendSize / 10;

                            if (WindowSizeCurrent > WindowResendSize)
                            {
                                WindowSizeCurrent = WindowResendSize;
                            }
                        }

                        while (sequence >= windowSeq)
                        {
                            acked++;
                            _rolloverWindowPacketList.Remove(packet);

                            if (_rolloverWindowPacketList.Count == 0)
                            {
                                windowPacket = null;
                                windowSeq = 0;

                                if (_newRolloverWindowPacketList.Count == 0)
                                {
                                    _outSequenceRollover = false;
                                }


                                break;
                            }

                            windowPacket = _windowPacketList[0];
                            _windowPacketList.RemoveAt(0);
                            windowPacket.ReadIndex = 2;
                            windowSeq = windowPacket.ReadNetworkUInt16();
                        }
                        _lastRemotePacketAckReceived = DateTimeExt.GetStoredMilliseconds();
                    }

                    return;
                }
            }

            if (_windowPacketList.Count == 0)
            {
                return;
            }


            windowPacket = _windowPacketList.First();
            windowPacket.ReadIndex = 2;
            windowSeq = windowPacket.ReadNetworkUInt16();

            if (sequence < windowSeq + _windowPacketList.Count)
            {
                if (WindowSizeCurrent < WindowResendSize)
                {
                    WindowSizeCurrent += WindowResendSize / 10;

                    if (WindowSizeCurrent > WindowResendSize)
                    {
                        WindowSizeCurrent = WindowResendSize;
                    }
                }

                while (sequence >= windowSeq)
                {
                    acked++;
                    _windowPacketList.Remove(windowPacket);

                    if (_windowPacketList.Count == 0)
                        break;

                    windowPacket = _windowPacketList.FirstOrDefault();

                    if (windowPacket == null)
                        break;

                    windowPacket.ReadIndex = 2;
                    windowSeq = windowPacket.ReadNetworkUInt16();
                }

                _lastRemotePacketAckReceived = DateTimeExt.GetStoredMilliseconds();
            }
        }


        protected void _ProcessPingPacket(Packet packet)
        {
            /*UInt64 now = DateTimeExt.GetStoredMilliseconds();
            UInt64 diff = now - _lastPingPacketRecieved;

            if (diff < 1000)
            {
                return;
            }

            _lastPingPacketRecieved = DateTimeExt.GetStoredMilliseconds();

            if (packet.Size == 5)
            {
                
            }*/
        }


        protected void _ProcessNetStatResponsePacket(Packet packet)
        {
            packet.ReadIndex = 2;
            UInt16 tick = packet.ReadNetworkUInt16();
            UInt32 serverTick = packet.ReadNetworkUInt32();
            ulong cPacketsSent = packet.ReadNetworkUInt64();
            ulong cPacketsRecieved = packet.ReadNetworkUInt64();
            ulong sPacketSent = packet.ReadNetworkUInt64();
            ulong sPacketRecieved = packet.ReadNetworkUInt64();

            _logger.Debug(
                    "Netstat Res (tick: {0}, serverTick: {1}, client sent: {2}, client recieved: {3}. server sent: {4}. server recieved: {5})",
                    tick,
                    serverTick,
                    cPacketsSent,
                    cPacketsRecieved,
                    sPacketSent,
                    sPacketRecieved);

        }


        protected void _ProcessDataChannelB(Packet packet)
        {
            byte priority = 0;
            byte routed = 0;
            byte dest = 0;
            UInt32 accountId = 0;

            packet.ReadIndex = sizeof(UInt32) * 2;

            priority = packet.ReadByte();
            routed = packet.ReadByte();

            if (routed == 0x19)
            {
                Int32 size = Convert.ToInt32(packet.ReadByte());

                do
                {
                    if (size == 0xff)
                    {
                        size = Convert.ToInt32(packet.ReadNetworkUInt16());
                    }

                    priority = packet.ReadByte();
                    accountId = packet.ReadUInt32();

                    Message msg = new Message(packet.Data, packet.ReadIndex, size - 7)
                        {
                                Priority = priority,
                                Routed = true
                        };

                    _AddIncomingMessage(msg, priority);
                    packet.ReadIndex += size - 7;
                    size = Convert.ToInt32(packet.ReadByte());
                }
                while (packet.ReadIndex < packet.Size && size != 0);
            }
            else
            {
                dest = packet.ReadByte();
                accountId = packet.ReadUInt32();

                Message msg = new Message(packet.Data, packet.ReadIndex, packet.Size - packet.ReadIndex)
                    {
                            Priority = priority,
                            Routed = true,
                    };
                _AddIncomingMessage(msg, priority);
            }

            _inSequenceNext++;
            _sendDelayedAcks = true;
        }



        protected void _ProcessDataChannelPacket(Packet packet, bool b)
        {
            /*byte priority = 0;
            byte routed = 0;
            byte dest = 0;
            UInt32 accountId = 0;

            packet.ReadIndex = 0;

            UInt16 pcaketType = packet.ReadUInt16();
            UInt16 sequence = packet.ReadNetworkUInt16();

            priority = packet.ReadByte();
            routed = packet.ReadByte();

            if (routed == 0x01)
            {
                dest = packet.ReadByte();
                accountId = packet.ReadUInt32();
            }

            if (routed == 0x19)
            {
                UInt32 size = packet.ReadByte();
                do
                {
                    if (size == 0xff)
                    {
                        size = packet.ReadNetworkUInt16();
                    }

                    priority = packet.ReadByte();
                    packet.ReadByte();

                    Message msg = new Message(packet.Data, packet.ReadIndex, Convert.ToInt32(size) - 2)
                        {
                                Priority = priority,
                        };
                    _AddIncomingMessage(msg, priority);
                    packet.ReadIndex += Convert.ToInt32(size) - 2;
                    size = Convert.ToUInt32(packet.ReadByte());
                }
                while (packet.ReadIndex < packet.Size && size != 0);
            }
            else
            {

            }*/

            packet.ReadIndex = 4;

            UInt16 multiPacket = packet.PeekNetworkUInt16();

            if (multiPacket == 0x0019)
            {
                packet.ReadIndex += 2;

                Int32 subPacketSize = packet.ReadByte();
                
                if (subPacketSize == 255)
                {
                    subPacketSize += packet.ReadByte();
                }

                do
                {
                    Message subMessage = new Message(packet.Data, packet.ReadIndex, subPacketSize);
                    _AddIncomingMessage(subMessage, 0);
                    packet.ReadIndex += subPacketSize;
                    subPacketSize = packet.ReadByte();

                    if (subPacketSize == 255)
                    {
                        subPacketSize += packet.ReadByte();
                    }
                }
                while (subPacketSize > 0 && packet.ReadIndex < packet.Size);

            }
            else
            {
                _AddIncomingMessage(new Message(packet.Data, 4, packet.Size - 4), 0);
            }
            
            _inSequenceNext++;
            _sendDelayedAcks = true;
        }


        protected void _ProcessRoutedFragmentedPacket(Packet packet)
        {
            packet.ReadIndex = 2;

            UInt16 sequence = packet.ReadNetworkUInt16();

            byte priority = packet.ReadByte();
            byte dest = packet.ReadByte();
            UInt32 accountId = packet.ReadUInt32();

            _inSequenceNext++;
            _sendDelayedAcks = true;

            if (_routedFragmentedPacketTotalSize == 0)
            {
                _routedFragmentedPacketTotalSize = packet.ReadNetworkUInt32();
                _routedFragmentedPacketCurrentSize = Convert.ToUInt32(packet.Size) - 8;

                _routedFragmentedPacketCurrentSequence = sequence;
                _routedFragmentedPacketStartSequence = sequence;

                _incomingRoutedFragmentedPacketQueue.Enqueue(packet);
            }
            else
            {
                _routedFragmentedPacketCurrentSize = Convert.ToUInt32(packet.Size) - 4;
                _routedFragmentedPacketCurrentSequence = sequence;

                if (_routedFragmentedPacketCurrentSize == _routedFragmentedPacketTotalSize)
                {
                    _incomingRoutedFragmentedPacketQueue.Enqueue(packet);

                    Packet fragment = null;

                    bool first = true;

                    Message msg = new Message(_routedFragmentedPacketTotalSize);
                    while (_incomingRoutedFragmentedPacketQueue.Count != 0)
                    {
                        fragment = _incomingRoutedFragmentedPacketQueue.Dequeue();

                        if (first)
                        {
                            first = false;
                            fragment.ReadIndex = 8;

                            priority = fragment.ReadByte();
                            fragment.ReadByte();
                            dest = fragment.ReadByte();
                            accountId = fragment.ReadUInt32();

                            msg.AddData(fragment.Data, 15);
                        }
                        else
                        {
                            msg.AddData(fragment.Data, 4);
                        }
                    }

                    msg.Routed = true;
                    msg.Priority = priority;

                    if (priority > 0x10)
                    {
                        return;
                    }

                    _AddIncomingMessage(msg, priority);

                    _routedFragmentedPacketTotalSize = 0;
                    _routedFragmentedPacketCurrentSize = 0;
                    _routedFragmentedPacketCurrentSequence = 0;
                    _routedFragmentedPacketStartSequence = 0;

                }
                else
                {
                    _incomingRoutedFragmentedPacketQueue.Enqueue(packet);
                }
            }

        }


        protected void _ProcessFragmentedPacket(Packet packet)
        {
            packet.ReadIndex = 2;

            UInt16 sequence = packet.ReadNetworkUInt16();
            byte priority = 0;
            byte routed = 0;
            byte dest = 0;
            UInt32 accountId = 0;

            if (sequence < _inSequenceNext)
            {
                return;
            }
            else if (sequence > _inSequenceNext)
            {
                return;
            }

            _inSequenceNext++;

            _sendDelayedAcks = true;

            if (_fragmentedPacketTotalSize == 0)
            {
                _fragmentedPacketTotalSize = packet.ReadNetworkUInt32();

                _fragmentedPacketCurrentSize = Convert.ToUInt32( packet.Size - 8);
                _fragmentedPacketCurrentSequence = sequence;
                _fragmentedPacketStartSequence = sequence;

                packet.ReadIndex = 8;

                priority = packet.ReadByte();

                _incomingFragmentedPacketQueue.Enqueue(packet);
            }
            else
            {
                _fragmentedPacketCurrentSize += Convert.ToUInt32(packet.Size - 4);
                _fragmentedPacketCurrentSequence = sequence;

                if (_fragmentedPacketCurrentSize >= _fragmentedPacketTotalSize)
                {
                    _incomingFragmentedPacketQueue.Enqueue(packet);

                    Message msg = new Message(_fragmentedPacketTotalSize);
                    Packet fragment = null;

                    bool first = true;

                    while (_incomingFragmentedPacketQueue.Count != 0)
                    {
                        fragment = _incomingFragmentedPacketQueue.Dequeue();
                        if (first)
                        {
                            first = false;
                            fragment.ReadIndex = 8;
                            priority = fragment.ReadByte();
                            routed = fragment.ReadByte();

                            if (routed != 0)
                            {
                                dest = fragment.ReadByte();
                                accountId = fragment.ReadUInt32();
                                msg.AddData(fragment.Data, 15);
                            }
                            else
                            {
                                msg.AddData(fragment.Data, 10, fragment.Size -8);
                            }
                        }
                        else
                        {
                            msg.AddData(fragment.Data, 4, fragment.Size -4);
                        }
                    }

                    msg.Priority = priority;
                    msg.Routed = routed != 0;

                    if (priority > 0x10)
                    {
                        return;
                    }

                    _AddIncomingMessage(msg, priority);

                    _fragmentedPacketTotalSize = 0;
                    _fragmentedPacketCurrentSize = 0;
                    _fragmentedPacketCurrentSequence = 0;
                    _fragmentedPacketStartSequence = 0;
                }
                else
                {
                    _incomingFragmentedPacketQueue.Enqueue(packet);
                }



            }
        }


        protected void _ProcessConnectCommand()
        {
            if (Status == SessionStatus.Initialize)
            {
                Status = SessionStatus.Connecting;
                _connectStart = DateTime.UtcNow.GetMilliseconds();
                _lastConnectAttempt = 0;
            }

            if (Status == SessionStatus.Connecting && Command == SessionCommand.Connect)
            {
                if (DateTime.UtcNow.GetMilliseconds() - _lastConnectAttempt > 5000)
                {
                    if (DateTime.UtcNow.GetMilliseconds() - _connectStart > 60000)
                    {
                        Status = SessionStatus.Initialize;
                        Command = SessionCommand.None;
                    }
                    else
                    {
                        _logger.Debug("connect packet");
                        _lastConnectAttempt = DateTime.UtcNow.GetMilliseconds();
                        if(RequestId == 0)
                            RequestId = (new Random(DateTime.UtcNow.Second)).Next();

                        Packet sessReqPacket = new Packet();
                        sessReqPacket.AddData((UInt16)SessionOp.SessionRequest);
                        sessReqPacket.AddNetworkData((UInt16)2); // crc size
                        sessReqPacket.AddData((UInt16)0u);
                        sessReqPacket.AddData(RequestId); // connection id
                        sessReqPacket.AddNetworkData(MaxPacketSize); // max packet size

                        _logger.Debug("Session request packet");
                        
                        sessReqPacket.Compressed = false;
                        sessReqPacket.Encrypted = false;

                        _AddOutgoingUnreliablePacket(sessReqPacket, true);
                    }
                }
            }
        }


        protected void _ProcessDisconectCommand()
        {
            _logger.Debug("disconnect packet");

            Status = SessionStatus.Disconnecting;
            Command = SessionCommand.None;

            Packet packet = new Packet();
            packet.AddData((UInt16)SessionOp.Disconnect);
            packet.AddNetworkData(RequestId);
            packet.AddNetworkData((UInt16)2);
            packet.Compressed = false;
            packet.Encrypted = true;

            _AddOutgoingUnreliablePacket(packet, true);
        }

        protected void _ResendData()
        {
            if (_windowPacketList.Count == 0)
            {
                return;
            }

            UInt64 localTime = DateTime.UtcNow.GetMilliseconds();
            UInt64 waitTime = 0;
            UInt64 oohTime = 0;
            Int32 packetsSent = 0;

            foreach (Packet rolloverPacket in _rolloverWindowPacketList)
            {
                if (rolloverPacket.TimeSent == 0)
                {
                   break;
                }

                waitTime = localTime - rolloverPacket.TimeSent;
                oohTime = localTime - rolloverPacket.OOHTimeSent;

                if (waitTime > 700 && oohTime > 700)
                {
                    rolloverPacket.OOHTimeSent = localTime;
                    _AddOutgoingReliablePacket(rolloverPacket);
                    packetsSent++;
                }
                else
                {
                    break;
                }
            }

            localTime = DateTime.UtcNow.GetMilliseconds();

            foreach (Packet windowPacket in _windowPacketList)
            {
                if (windowPacket.TimeSent == 0)
                {
                    break;
                }

                waitTime = localTime - windowPacket.TimeSent;
                oohTime = localTime - windowPacket.OOHTimeSent;

                if (waitTime > 700 && oohTime > 700)
                {
                    windowPacket.OOHTimeSent = localTime;
                    _AddOutgoingReliablePacket(windowPacket);
                    packetsSent++;
                }
                else
                {
                    break;
                }
            }
        }

        protected void _BuildOutgoingReliableRoutedPackets(Message message)
        {
            Packet newPacket = null;
            UInt16 messageIndex = 0;
            UInt16 envelopSize = 18;
            UInt16 messageSize = Convert.ToUInt16(message.Size);

            if (messageSize + envelopSize > MaxPacketSize)
            {
                newPacket = new Packet(MaxPacketSize);
                newPacket.AddData(Convert.ToUInt16(SessionOp.DataFrag2));
                newPacket.AddNetworkData(_outSequenceNext);
                newPacket.AddNetworkData(messageSize + 7);
                newPacket.AddData(message.Priority);
                newPacket.AddData((byte)1);
                newPacket.AddData((byte)0);
                newPacket.AddData((UInt32)0);
                newPacket.AddData(message.Data, 0, Convert.ToInt32(MaxPacketSize - envelopSize));
                messageIndex += Convert.ToUInt16(MaxPacketSize - envelopSize);

                newPacket.Size = newPacket.WriteIndex;

                newPacket.Compressed = false;
                newPacket.Encrypted = true;

                _newWindowPacketList.Add(newPacket);

                if (_outSequenceNext == UInt16.MaxValue)
                {
                    _HandleOutSequenceRollover();
                }
                else
                {
                    ++_outSequenceNext;
                }

                while (messageSize > messageIndex)
                {
                    var dataSize = Convert.ToInt32(Math.Min(MaxPacketSize - 7, messageSize - messageIndex));

                    newPacket = new Packet(messageSize - messageIndex);
                    newPacket.AddData(Convert.ToUInt16(SessionOp.DataFrag2));
                    newPacket.AddNetworkData(_outSequenceNext);
                    newPacket.AddData(message.Data, messageIndex, dataSize);
                    messageIndex += Convert.ToUInt16(MaxPacketSize - 7);

                    newPacket.Size = newPacket.WriteIndex;

                    newPacket.Compressed = false;
                    newPacket.Encrypted = true;

                    _newWindowPacketList.Add(newPacket);

                    if (_outSequenceNext == UInt16.MaxValue)
                    {
                        _HandleOutSequenceRollover();
                    }
                    else
                    {
                        ++_outSequenceNext;
                    }


                }
            }
            else
            {
                newPacket = new Packet(message.Size);
                newPacket.AddNetworkData(Convert.ToUInt16(SessionOp.DataFrag2));
                newPacket.AddNetworkData(_outSequenceNext);
                newPacket.AddData(message.Priority);
                newPacket.AddData((byte)(message.Routed ? 1 : 0));
                newPacket.AddData((byte)0);
                newPacket.AddData((UInt32)0);
                newPacket.AddData(message.Data, 0, message.Size);

                newPacket.Compressed = false;
                newPacket.Encrypted = true;

                newPacket.Size = newPacket.WriteIndex;
                
                _newWindowPacketList.Add(newPacket);

                if (_outSequenceNext == UInt16.MaxValue)
                {
                    _HandleOutSequenceRollover();
                }
                else
                {
                    ++_outSequenceNext;
                }
            }
        }

        protected void _BuildOutgoingReliablePackets(Message message)
        {
            _logger.Debug("build outgoing reliable packet. Sequence: {0}", _outSequenceNext);
            
            UInt16 messageIndex = 0;
            UInt16 envelopSize = 13;
            UInt16 messageSize = Convert.ToUInt16(message.Size);

            if (messageSize + envelopSize > MaxPacketSize)
            {
                _logger.Debug("Fragmented Packet");
                Packet fragmentedPacket = new Packet(MaxPacketSize);
                fragmentedPacket.AddData((UInt16)SessionOp.DataFrag1);
                fragmentedPacket.AddNetworkData(_outSequenceNext);
                fragmentedPacket.AddNetworkData(messageSize + 2);
                fragmentedPacket.AddData(message.Priority);
                fragmentedPacket.AddData((byte)0);
                fragmentedPacket.AddData(message.Data, 0, Convert.ToInt32(MaxPacketSize - envelopSize));
                messageIndex += Convert.ToUInt16(MaxPacketSize - envelopSize);
                
                fragmentedPacket.Compressed = false;
                fragmentedPacket.Encrypted = true;

                fragmentedPacket.Size = fragmentedPacket.WriteIndex;                
                
                _newWindowPacketList.Add(fragmentedPacket);

                if (_outSequenceNext == UInt16.MaxValue)
                {
                    _HandleOutSequenceRollover();
                }
                else
                {
                    ++_outSequenceNext;
                }

                while (messageSize > messageIndex)
                {
                    var dataSize = Convert.ToInt32(Math.Min(MaxPacketSize - 7, messageSize - messageIndex));

                    fragmentedPacket = new Packet();
                    fragmentedPacket.AddData((UInt16)SessionOp.DataFrag1);
                    fragmentedPacket.AddNetworkData((ushort)_outSequenceNext);
                    fragmentedPacket.AddData(message.Data, messageIndex, dataSize);
                    messageIndex += Convert.ToUInt16(MaxPacketSize - 7);

                    fragmentedPacket.Compressed = false;
                    fragmentedPacket.Encrypted = true;

                    fragmentedPacket.Size = fragmentedPacket.WriteIndex;

                    _newWindowPacketList.Add(fragmentedPacket);

                    if (_outSequenceNext == UInt16.MaxValue)
                    {
                        _HandleOutSequenceRollover();
                    }
                    else
                    {
                        ++_outSequenceNext;
                    }


                }
            }
            else
            {
                //newPacket = new Packet(message.Size + 4);
                Packet dataPacket = new Packet();
                dataPacket.AddData((UInt16)SessionOp.DataChannel1);
                dataPacket.AddNetworkData((ushort)_outSequenceNext); //sequence
                dataPacket.AddData(message.Data, 0, message.Size);

                dataPacket.Compressed = true;
                dataPacket.Encrypted = true;

                dataPacket.Size = dataPacket.WriteIndex;

                _newWindowPacketList.Add(dataPacket);

                if (_outSequenceNext == UInt16.MaxValue)
                {
                    _HandleOutSequenceRollover();
                }
                else
                {
                    ++_outSequenceNext;
                }
            }            
        }


        protected void _BuildOutoingUnreliablePakets(Message message)
        {
            Packet toSend = new Packet(message.Size);
            /*toSend.AddData(message.Priority);
            toSend.AddData((byte)(message.Routed ? 1 : 0));
            if (message.Routed)
            {
                toSend.AddData((byte)0);
                toSend.AddData((UInt32)0);
            }*/

            toSend.AddData(message.Data, 0, message.Size);

            toSend.Compressed = false;
            toSend.Encrypted = true;

            _AddOutgoingUnreliablePacket(toSend, true);

        }

        protected UInt32 _BuildPackets()
        {
            UInt32 packetsBuilt = 0;

            if (_outgoingMessageQueue.Count == 0)
            {
                return packetsBuilt;
            }


            Message msg = _outgoingMessageQueue.Dequeue();

            if (_outgoingMessageQueue.Count == 0 || _outgoingMessageQueue.Peek().Size > MaxPacketSize - 21)
            {
                packetsBuilt++;

                if (msg.Routed)
                {
                    _BuildOutgoingReliableRoutedPackets(msg);
                }
                else
                {
                    _BuildOutgoingReliablePackets(msg);
                }
            }
            else
            {
                if (msg.Routed)
                {
                    _routedMultiMessageQueue.Enqueue(msg);

                    UInt16 baseSize = Convert.ToUInt16(19 + msg.Size);
                    packetsBuilt++;
                    while (baseSize < MaxPacketSize && _outgoingMessageQueue.Count > 0)
                    {
                        baseSize += Convert.ToUInt16(_outgoingMessageQueue.Peek().Size + 10);

                        if (baseSize > MaxPacketSize)
                        {
                            break;
                        }

                        _routedMultiMessageQueue.Enqueue(_outgoingMessageQueue.Dequeue());
                    }

                    _BuildRoutedMultiDataPacket();
                }
                else
                {
                    _multiMessageQueue.Enqueue(msg);

                    UInt16 baseSize = Convert.ToUInt16(14 + msg.Size);
                    packetsBuilt++;
                    while (baseSize < MaxPacketSize && _outgoingMessageQueue.Count > 0)
                    {
                        baseSize += Convert.ToUInt16(_outgoingMessageQueue.Peek().Size + 5);

                        if(baseSize >= MaxPacketSize)
                            break;
                        _multiMessageQueue.Enqueue(_outgoingMessageQueue.Dequeue());
                    }

                    _BuildMultiDataPacket();
                }
            }

            return packetsBuilt;
        }

        public UInt32 _BuildUreliablePackets()
        {
            UInt32 packetsBuilt = 0;
            Message message = null;
            if (!_unreliableMessageQueue.TryDequeue(out message))
            {
                return 0;
            }
            Message frontMessage = null;
            bool front = _unreliableMessageQueue.TryPeek(out frontMessage);

            if (!front || message.Routed || message.Size > 252 || frontMessage.Size > 252
                || message.Size + frontMessage.Size > MaxUreliablePacketSize - 16)
            {
                packetsBuilt++;
                _BuildOutoingUnreliablePakets(message);
            }
            else
            {
                UInt16 baseSize = Convert.ToUInt16(12 + message.Size);
                _unreliableMultiMessageQueue.Enqueue(message);
                packetsBuilt++;
                while (baseSize < MaxUreliablePacketSize && _unreliableMessageQueue.TryPeek(out frontMessage))
                {
                    baseSize += Convert.ToUInt16(3 + message.Size);
                    if (baseSize > MaxPacketSize || message.Routed || message.Size > 252)
                    {
                        break;
                    }

                    _unreliableMessageQueue.TryDequeue(out message);
                    _unreliableMultiMessageQueue.Enqueue(message);
                }

                _BuildUnreliableMultiDataPacket();
            }
            return packetsBuilt;
        }


        private void _BuildUnreliableMultiDataPacket()
        {
            Packet packet = new Packet();
            Message message = null;

            packet.AddData(Convert.ToUInt16(SessionOp.MultiPacket));

            while (_unreliableMultiMessageQueue.Count != 0)
            {
                message = _unreliableMultiMessageQueue.Dequeue();
                packet.AddData(Convert.ToUInt16(message.Size + 2));
                packet.AddData(message.Priority);
                packet.AddData((byte)0);
                packet.AddData(message.Data, 0, message.Size);
            }

            packet.Compressed = true;
            packet.Encrypted = true;

            _AddOutgoingUnreliablePacket(packet, true);
        }


        private void _BuildMultiDataPacket()
        {
            Packet packet = new Packet();
            Message message = null;

            packet.AddData((UInt16)SessionOp.DataChannel1);
            packet.AddNetworkData(_outSequenceNext);
            packet.AddData((UInt16)0x1900);

            while (_multiMessageQueue.Count > 0)
            {
                message = _multiMessageQueue.Dequeue();
                if (message.Size + 2 > 254)
                {
                    packet.AddData((byte)0xff);
                    packet.AddNetworkData(Convert.ToUInt16(message.Size + 2));
                }
                else
                {
                    packet.AddData((byte)(message.Size + 2));
                }

                packet.AddData(message.Priority);
                packet.AddData((byte)0);
                packet.AddData(message.Data, 0, message.Size);
            }

            packet.Size = packet.WriteIndex;

            packet.Compressed = true;
            packet.Encrypted = true;


            _newWindowPacketList.Add(packet);

            if (_outSequenceNext == UInt16.MaxValue)
            {
                _HandleOutSequenceRollover();
            }
            else
            {
                ++_outSequenceNext;
            }

        }


        private void _BuildRoutedMultiDataPacket()
        {
            Packet packet = new Packet();
            Message message = null;

            packet.AddData(Convert.ToUInt16(SessionOp.DataChannel2));
            packet.AddNetworkData(_outSequenceNext);
            packet.AddData((UInt16)0x1900);

            while (_routedMultiMessageQueue.Count != 0)
            {
                message = _routedMultiMessageQueue.Dequeue();

                if (message.Size + 7 > 254)
                {
                    packet.AddData((byte)0xff);
                    packet.AddNetworkData((UInt16)message.Size + 7);
                }
                else
                {
                    packet.AddData((byte)message.Size + 7);
                }

                packet.AddData(message.Priority);
                packet.AddData((UInt32)0);

                packet.AddData(message.Data, 0,message.Size);
            }

            packet.Size = packet.WriteIndex;
            
            packet.Compressed = true;
            packet.Encrypted = true;

            _newWindowPacketList.Add(packet);

            if (_outSequenceNext == UInt16.MaxValue)
            {
                _HandleOutSequenceRollover();
            }
            else
            {
                ++_outSequenceNext;
            }
        }


        protected void _HandleOutSequenceRollover()
        {
            _outSequenceRollover = true;

            _rolloverWindowPacketList = _windowPacketList;
            _newRolloverWindowPacketList = _newWindowPacketList;

            _windowPacketList.Clear();
            _newWindowPacketList.Clear();

        }
    }
}
