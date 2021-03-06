﻿using SWG.Client.Network;

namespace SWG.Client.Utils.Attribute
{
    public class RegisterDeltaMessageAttribute : RegisterMessageWithSecondaryAttribute
    {

        public RegisterDeltaMessageAttribute(uint opcode) 
            : base(opcode)
        {
            FactoryHandlerOpCodeEnum = MessageOp.DeltasMessage;
        }

        public RegisterDeltaMessageAttribute(MessageOp opcode)
            : this((uint)opcode)
        {

        }

        public RegisterDeltaMessageAttribute(uint opcode, uint secondary)
            : base(opcode, secondary)
        {
            FactoryHandlerOpCodeEnum = MessageOp.DeltasMessage;
        }

        public RegisterDeltaMessageAttribute(MessageOp opcode, uint secondary)
            : this((uint)opcode, secondary)
        {
        }

        public RegisterDeltaMessageAttribute()
        {
            FactoryHandlerOpCodeEnum = MessageOp.DeltasMessage;
        }
    }
}
