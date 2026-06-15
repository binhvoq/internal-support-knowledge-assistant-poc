import { expect, test } from '@playwright/test';

test('creates a ticket, waits for auto suggestion, and resolves it from the UI', async ({ page }) => {
  await page.goto('/');

  await page.getByRole('button', { name: 'Employee' }).click();
  await page.locator('label:text("Employee ID") + input').fill('EMP-E2E');
  await page.locator('label:text("Category") + select').selectOption('IT');
  await page.locator('label:text("Question") + textarea').fill(`Can ho tro reset mat khau VPN e2e ${Date.now()}`);
  await page.getByRole('button', { name: 'Tao ticket' }).click();

  const created = page.locator('.success', { hasText: /Da tao [a-f0-9]{32}/i });
  await expect(created).toBeVisible();
  const createdText = (await created.textContent()) ?? '';
  const ticketId = createdText.match(/[a-f0-9]{32}/i)?.[0];
  expect(ticketId).toBeTruthy();

  await page.getByRole('button', { name: 'Support Queue' }).click();
  const queueItem = page.getByRole('button', { name: new RegExp(`Open ticket ${ticketId}`) });
  await expect(queueItem).toBeVisible();
  await queueItem.click();

  await expect(page.getByRole('heading', { name: ticketId! })).toBeVisible();
  await expect(page.locator('body')).toContainText(/Auto suggestion|goi y/i);

  const finalAnswer = page.locator('label:text("Final / Edited Answer") + textarea');
  await expect
    .poll(async () => (await finalAnswer.inputValue()).trim().length, {
      timeout: 45_000,
      message: 'auto suggestion should populate the final answer textarea',
    })
    .toBeGreaterThan(0);

  await finalAnswer.fill(`E2E resolved answer for ${ticketId}`);
  await page.getByRole('button', { name: 'Resolve' }).click();
  await expect(page.locator('p', { hasText: 'Status:' })).toContainText('Resolved');
});
