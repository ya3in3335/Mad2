package com.veltrix.os

import com.veltrix.os.data.db.ProductEntity
import com.veltrix.os.data.repo.CartLine
import org.junit.Assert.assertEquals
import org.junit.Test

class CartTest {
    private val p = ProductEntity("1", "SKU", "Item", null, "Physical", "Active", null, 19.0, 1190.0, true, "", "2026-01-01")

    @Test fun lineTotalsWithDiscount() {
        val l = CartLine(p, 2.0, 10.0)
        assertEquals(2380.0, l.gross, 0.001)
        assertEquals(238.0, l.discount, 0.001)
        assertEquals(2142.0, l.total, 0.001)
    }
}
