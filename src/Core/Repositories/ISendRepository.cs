﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bit.Core.Entities;

namespace Bit.Core.Repositories
{
    public interface ISendRepository : IRepository<Send, Guid>
    {
        Task<ICollection<Send>> GetManyByUserIdAsync(Guid userId);
        Task<ICollection<Send>> GetManyByDeletionDateAsync(DateTime deletionDateBefore);
    }
}
