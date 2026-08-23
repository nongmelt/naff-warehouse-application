-- Seed matrix for Duplicate Order? card tweaks (QADUP0002–QADUP0006).
-- Target DB: warehouse_snapshot_qa ONLY. Idempotent: wipes and re-creates
-- its own rows. QADUP0001 (original seed) is untouched.
BEGIN;

DELETE FROM import_rows   WHERE order_number IN ('QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006');
DELETE FROM packing_lists WHERE order_number IN ('QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006');

-- One order line per order: ordered_qty = 2. Parcel items sum to 6 (> 2) so
-- the reissue overflow fires for every pair.
INSERT INTO import_rows (batch_id, platform, raw_data, natural_key)
SELECT 354, 'Shopee',
       jsonb_build_object(
         'order_number', o,
         'quantity', '2',
         'seller_sku', 'QASKU1',
         'product_name', 'QA Duplicate Test Item',
         'shipping_options', 'Instant Delivery - QA seed (ส่งทันที)'),
       'QA:' || o || '|seed'
FROM unnest(ARRAY['QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006']) AS o;

-- Shared item payloads
-- original (3 units): QASKU1 x2 + QASKU2 x1
-- zeroed:   both quantities 0  (fully QC-verified)
-- partial:  QASKU1 0 (verified), QASKU2 1 (not yet)

-- QADUP0002 — sibling QC Passed
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, updated_product_lists, packed_by, packed_at, checked_by, checked_at,
   created_at, updated_at)
VALUES
('QADUPSIB0002','QADUP0002',3,'QC Passed','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '{"items": [{"quantity": 0, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 0, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '5 hours', '26BKKPK068', now() - interval '4 hours',
 now() - interval '6 hours', now() - interval '4 hours');

-- QADUP0003 — sibling Shipped with full QC trail (dual-pill case)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, updated_product_lists, packed_by, packed_at, checked_by, checked_at,
   shipped_by, shipped_at, created_at, updated_at)
VALUES
('QADUPSIB0003','QADUP0003',3,'Shipped','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '{"items": [{"quantity": 0, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 0, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '7 hours', '26BKKPK068', now() - interval '6 hours',
 '25BKKPK049', now() - interval '2 hours',
 now() - interval '8 hours', now() - interval '2 hours');

-- QADUP0004 — sibling Packed, never QC'd (no green expected)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, packed_by, packed_at, created_at, updated_at)
VALUES
('QADUPSIB0004','QADUP0004',3,'Packed','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '3 hours',
 now() - interval '4 hours', now() - interval '3 hours');

-- QADUP0005 — sibling ALSO To be packed (neither-processed banner case)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, created_at)
VALUES
('QADUPSIB0005','QADUP0005',3,'To be packed','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 now() - interval '90 minutes');

-- QADUP0006 — sibling QC Hold, partially verified (mixed tiles)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, updated_product_lists, packed_by, packed_at, checked_by, checked_at,
   created_at, updated_at)
VALUES
('QADUPSIB0006','QADUP0006',3,'QC Hold','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '{"items": [{"quantity": 0, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '2 hours', '26BKKPK068', now() - interval '1 hour',
 now() - interval '3 hours', now() - interval '1 hour');

-- Scan legs: all To be packed, 3 units each, created "just now"
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, created_at)
SELECT 'QADUPSCN' || right(o, 4), o, 3, 'To be packed', 'Shopee',
       'Instant Delivery - QA seed (ส่งทันที)',
       '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
       now() - interval '10 minutes'
FROM unnest(ARRAY['QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006']) AS o;

COMMIT;
