﻿namespace FSH.WebAPI.Domain.Common.Contracts;

public interface IEntity
{
}

public interface IEntity<T> : IEntity
{
    T Id { get; }
}